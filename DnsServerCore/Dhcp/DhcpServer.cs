﻿/*
Technitium DNS Server
Copyright (C) 2019  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.Dhcp.Options;
using DnsServerCore.Dns;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Dhcp
{
    //Dynamic Host Configuration Protocol
    //https://tools.ietf.org/html/rfc2131

    //DHCP Options and BOOTP Vendor Extensions
    //https://tools.ietf.org/html/rfc2132

    //Encoding Long Options in the Dynamic Host Configuration Protocol (DHCPv4)
    //https://tools.ietf.org/html/rfc3396

    //Client Fully Qualified Domain Name(FQDN) Option
    //https://tools.ietf.org/html/rfc4702

    public class DhcpServer : IDisposable
    {
        #region enum

        enum ServiceState
        {
            Stopped = 0,
            Starting = 1,
            Running = 2,
            Stopping = 3
        }

        #endregion

        #region variables

        readonly string _configFolder;

        readonly List<Socket> _udpListeners = new List<Socket>();
        readonly List<Thread> _listenerThreads = new List<Thread>();

        readonly ConcurrentDictionary<string, Scope> _scopes = new ConcurrentDictionary<string, Scope>();

        Zone _authoritativeZoneRoot;
        LogManager _log;

        int _serverAnyAddressScopeCount;
        volatile ServiceState _state = ServiceState.Stopped;

        Timer _maintenanceTimer;
        const int MAINTENANCE_TIMER_INTERVAL = 10000;

        DateTime _lastModifiedScopesSavedOn;

        #endregion

        #region constructor

        public DhcpServer(string configFolder)
        {
            _configFolder = configFolder;

            if (!Directory.Exists(_configFolder))
                Directory.CreateDirectory(_configFolder);
        }

        #endregion

        #region IDisposable

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_maintenanceTimer != null)
                    _maintenanceTimer.Dispose();

                Stop();

                SaveModifiedScopes();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        #region private

        private void ReadUdpRequestAsync(object parameter)
        {
            Socket udpListener = parameter as Socket;
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] recvBuffer = new byte[576];
            int bytesRecv;

            try
            {
                while (true)
                {
                    try
                    {
                        bytesRecv = udpListener.ReceiveFrom(recvBuffer, ref remoteEP);
                    }
                    catch (SocketException ex)
                    {
                        switch (ex.SocketErrorCode)
                        {
                            case SocketError.ConnectionReset:
                            case SocketError.HostUnreachable:
                            case SocketError.MessageSize:
                            case SocketError.NetworkReset:
                                bytesRecv = 0;
                                break;

                            default:
                                throw;
                        }
                    }

                    if (bytesRecv > 0)
                    {
                        switch ((remoteEP as IPEndPoint).Port)
                        {
                            case 67:
                            case 68:
                                try
                                {
                                    ThreadPool.QueueUserWorkItem(ProcessUdpRequestAsync, new object[] { udpListener, remoteEP, new DhcpMessage(new MemoryStream(recvBuffer, 0, bytesRecv, false)) });
                                }
                                catch (Exception ex)
                                {
                                    LogManager log = _log;
                                    if (log != null)
                                        log.Write(remoteEP as IPEndPoint, ex);
                                }

                                break;
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                switch (ex.SocketErrorCode)
                {
                    case SocketError.Interrupted:
                        break; //server stopping

                    default:
                        LogManager log = _log;
                        if (log != null)
                            log.Write(remoteEP as IPEndPoint, ex);

                        throw;
                }
            }
            catch (Exception ex)
            {
                if ((_state == ServiceState.Stopping) || (_state == ServiceState.Stopped))
                    return; //server stopping

                LogManager log = _log;
                if (log != null)
                    log.Write(remoteEP as IPEndPoint, ex);

                throw;
            }
        }

        private void ProcessUdpRequestAsync(object parameter)
        {
            object[] parameters = parameter as object[];

            Socket udpListener = parameters[0] as Socket;
            EndPoint remoteEP = parameters[1] as EndPoint;
            DhcpMessage request = parameters[2] as DhcpMessage;

            try
            {
                DhcpMessage response = ProcessDhcpMessage(request, remoteEP as IPEndPoint, udpListener.LocalEndPoint as IPEndPoint);

                //send response
                if (response != null)
                {
                    byte[] sendBuffer = new byte[512];
                    MemoryStream sendBufferStream = new MemoryStream(sendBuffer);

                    response.WriteTo(sendBufferStream);

                    //send dns datagram
                    if (!request.RelayAgentIpAddress.Equals(IPAddress.Any))
                    {
                        //received request via relay agent so send unicast response to relay agent on port 67
                        udpListener.SendTo(sendBuffer, 0, (int)sendBufferStream.Position, SocketFlags.None, new IPEndPoint(request.RelayAgentIpAddress, 67));
                    }
                    else if (!request.ClientIpAddress.Equals(IPAddress.Any))
                    {
                        //client is already configured and renewing lease so send unicast response on port 68
                        udpListener.SendTo(sendBuffer, 0, (int)sendBufferStream.Position, SocketFlags.None, new IPEndPoint(request.ClientIpAddress, 68));
                    }
                    else
                    {
                        //send response as broadcast on port 68
                        udpListener.SendTo(sendBuffer, 0, (int)sendBufferStream.Position, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, 68));
                    }
                }
            }
            catch (Exception ex)
            {
                if ((_state == ServiceState.Stopping) || (_state == ServiceState.Stopped))
                    return; //server stopping

                LogManager log = _log;
                if (log != null)
                    log.Write(remoteEP as IPEndPoint, ex);
            }
        }

        private DhcpMessage ProcessDhcpMessage(DhcpMessage request, IPEndPoint remoteEP, IPEndPoint interfaceEP)
        {
            if (request.OpCode != DhcpMessageOpCode.BootRequest)
                return null;

            switch (request.DhcpMessageType?.Type)
            {
                case DhcpMessageType.Discover:
                    {
                        Scope scope = FindScope(request, remoteEP.Address, interfaceEP.Address);
                        if (scope == null)
                            return null; //no scope available; do nothing

                        if (scope.OfferDelayTime > 0)
                            Thread.Sleep(scope.OfferDelayTime); //delay sending offer

                        Lease offer = scope.GetOffer(request);
                        if (offer == null)
                            throw new DhcpServerException("DHCP Server failed to offer address: address unavailable.");

                        List<DhcpOption> options = scope.GetOptions(request, interfaceEP.Address);
                        if (options == null)
                            return null;

                        //log ip offer
                        LogManager log = _log;
                        if (log != null)
                            log.Write(remoteEP as IPEndPoint, "DHCP Server offered IP address [" + offer.Address.ToString() + "] to " + request.GetClientFullIdentifier() + ".");

                        return new DhcpMessage(request, offer.Address, interfaceEP.Address, options);
                    }

                case DhcpMessageType.Request:
                    {
                        //request ip address lease or extend existing lease
                        Scope scope;
                        Lease leaseOffer;

                        if (request.ServerIdentifier == null)
                        {
                            if (request.RequestedIpAddress == null)
                            {
                                //renewing or rebinding

                                if (request.ClientIpAddress.Equals(IPAddress.Any))
                                    return null; //client must set IP address in ciaddr; do nothing

                                scope = FindScope(request, remoteEP.Address, interfaceEP.Address);
                                if (scope == null)
                                {
                                    //no scope available; do nothing
                                    return null;
                                }

                                leaseOffer = scope.GetExistingLeaseOrOffer(request);
                                if (leaseOffer == null)
                                {
                                    //no existing lease or offer available for client
                                    //send nak
                                    return new DhcpMessage(request, IPAddress.Any, interfaceEP.Address, new DhcpOption[] { new DhcpMessageTypeOption(DhcpMessageType.Nak), new ServerIdentifierOption(interfaceEP.Address), DhcpOption.CreateEndOption() });
                                }

                                if (!request.ClientIpAddress.Equals(leaseOffer.Address))
                                {
                                    //client ip is incorrect
                                    //send nak
                                    return new DhcpMessage(request, IPAddress.Any, interfaceEP.Address, new DhcpOption[] { new DhcpMessageTypeOption(DhcpMessageType.Nak), new ServerIdentifierOption(interfaceEP.Address), DhcpOption.CreateEndOption() });
                                }
                            }
                            else
                            {
                                //init-reboot
                                scope = FindScope(request, remoteEP.Address, interfaceEP.Address);
                                if (scope == null)
                                {
                                    //no scope available; do nothing
                                    return null;
                                }

                                leaseOffer = scope.GetExistingLeaseOrOffer(request);
                                if (leaseOffer == null)
                                {
                                    //no existing lease or offer available for client
                                    //send nak
                                    return new DhcpMessage(request, IPAddress.Any, interfaceEP.Address, new DhcpOption[] { new DhcpMessageTypeOption(DhcpMessageType.Nak), new ServerIdentifierOption(interfaceEP.Address), DhcpOption.CreateEndOption() });
                                }

                                if (!request.RequestedIpAddress.Address.Equals(leaseOffer.Address))
                                {
                                    //the client's notion of its IP address is not correct - RFC 2131
                                    //send nak
                                    return new DhcpMessage(request, IPAddress.Any, interfaceEP.Address, new DhcpOption[] { new DhcpMessageTypeOption(DhcpMessageType.Nak), new ServerIdentifierOption(interfaceEP.Address), DhcpOption.CreateEndOption() });
                                }
                            }
                        }
                        else
                        {
                            //selecting offer

                            if (request.RequestedIpAddress == null)
                                return null; //client MUST include this option; do nothing

                            if (!request.ServerIdentifier.Address.Equals(interfaceEP.Address))
                                return null; //offer declined by client; do nothing

                            scope = FindScope(request, remoteEP.Address, interfaceEP.Address);
                            if (scope == null)
                            {
                                //no scope available
                                //send nak
                                return new DhcpMessage(request, IPAddress.Any, interfaceEP.Address, new DhcpOption[] { new DhcpMessageTypeOption(DhcpMessageType.Nak), new ServerIdentifierOption(interfaceEP.Address), DhcpOption.CreateEndOption() });
                            }

                            leaseOffer = scope.GetExistingLeaseOrOffer(request);
                            if (leaseOffer == null)
                            {
                                //no existing lease or offer available for client
                                //send nak
                                return new DhcpMessage(request, IPAddress.Any, interfaceEP.Address, new DhcpOption[] { new DhcpMessageTypeOption(DhcpMessageType.Nak), new ServerIdentifierOption(interfaceEP.Address), DhcpOption.CreateEndOption() });
                            }

                            if (!request.RequestedIpAddress.Address.Equals(leaseOffer.Address))
                            {
                                //requested ip is incorrect
                                //send nak
                                return new DhcpMessage(request, IPAddress.Any, interfaceEP.Address, new DhcpOption[] { new DhcpMessageTypeOption(DhcpMessageType.Nak), new ServerIdentifierOption(interfaceEP.Address), DhcpOption.CreateEndOption() });
                            }
                        }

                        List<DhcpOption> options = scope.GetOptions(request, interfaceEP.Address);
                        if (options == null)
                            return null;

                        scope.CommitLease(leaseOffer);

                        //log ip lease
                        LogManager log = _log;
                        if (log != null)
                            log.Write(remoteEP as IPEndPoint, "DHCP Server leased IP address [" + leaseOffer.Address.ToString() + "] to " + request.GetClientFullIdentifier() + ".");

                        if (!string.IsNullOrEmpty(scope.DomainName))
                        {
                            //update dns
                            string clientDomainName = null;

                            foreach (DhcpOption option in options)
                            {
                                if (option.Code == DhcpOptionCode.ClientFullyQualifiedDomainName)
                                {
                                    clientDomainName = (option as ClientFullyQualifiedDomainNameOption).DomainName;
                                    break;
                                }
                            }

                            if (clientDomainName == null)
                            {
                                if (request.HostName != null)
                                    clientDomainName = request.HostName.HostName + "." + scope.DomainName;
                            }

                            if (clientDomainName != null)
                            {
                                leaseOffer.SetHostName(clientDomainName.ToLower());
                                UpdateDnsAuthZone(true, scope, leaseOffer);
                            }
                        }

                        return new DhcpMessage(request, leaseOffer.Address, interfaceEP.Address, options);
                    }

                case DhcpMessageType.Decline:
                    {
                        //ip address is already in use as detected by client via ARP

                        if ((request.ServerIdentifier == null) || (request.RequestedIpAddress == null))
                            return null; //client MUST include these option; do nothing

                        if (!request.ServerIdentifier.Address.Equals(interfaceEP.Address))
                            return null; //request not for this server; do nothing

                        Scope scope = FindScope(request, remoteEP.Address, interfaceEP.Address);
                        if (scope == null)
                            return null; //no scope available; do nothing

                        Lease lease = scope.GetExistingLeaseOrOffer(request);
                        if (lease == null)
                            return null; //no existing lease or offer available for client; do nothing

                        if (!lease.Address.Equals(request.RequestedIpAddress.Address))
                            return null; //the client's notion of its IP address is not correct; do nothing

                        //remove lease since the IP address is used by someone else
                        scope.ReleaseLease(lease);

                        //log issue
                        LogManager log = _log;
                        if (log != null)
                            log.Write(remoteEP as IPEndPoint, "DHCP Server received DECLINE message: " + lease.GetClientFullIdentifier() + " detected that IP address [" + lease.Address + "] is already in use.");

                        //update dns
                        UpdateDnsAuthZone(false, scope, lease);

                        //do nothing
                        return null;
                    }

                case DhcpMessageType.Release:
                    {
                        //cancel ip address lease

                        if (request.ServerIdentifier == null)
                            return null; //client MUST include this option; do nothing

                        if (!request.ServerIdentifier.Address.Equals(interfaceEP.Address))
                            return null; //request not for this server; do nothing

                        Scope scope = FindScope(request, remoteEP.Address, interfaceEP.Address);
                        if (scope == null)
                            return null; //no scope available; do nothing

                        Lease lease = scope.GetExistingLeaseOrOffer(request);
                        if (lease == null)
                            return null; //no existing lease or offer available for client; do nothing

                        if (!lease.Address.Equals(request.ClientIpAddress))
                            return null; //the client's notion of its IP address is not correct; do nothing

                        //release lease
                        scope.ReleaseLease(lease);

                        //log ip lease release
                        LogManager log = _log;
                        if (log != null)
                            log.Write(remoteEP as IPEndPoint, "DHCP Server released IP address [" + lease.Address.ToString() + "] that was leased to " + lease.GetClientFullIdentifier() + ".");

                        //update dns
                        UpdateDnsAuthZone(false, scope, lease);

                        //do nothing
                        return null;
                    }

                case DhcpMessageType.Inform:
                    {
                        //need only local config; already has ip address assigned externally/manually

                        Scope scope = FindScope(request, remoteEP.Address, interfaceEP.Address);
                        if (scope == null)
                            return null; //no scope available; do nothing

                        List<DhcpOption> options = scope.GetOptions(request, interfaceEP.Address);
                        if (options == null)
                            return null;

                        //log inform
                        LogManager log = _log;
                        if (log != null)
                            log.Write(remoteEP as IPEndPoint, "DHCP Server received INFORM message from " + request.GetClientFullIdentifier() + ".");

                        return new DhcpMessage(request, IPAddress.Any, interfaceEP.Address, options);
                    }

                default:
                    return null;
            }
        }

        private Scope FindScope(DhcpMessage request, IPAddress remoteAddress, IPAddress interfaceAddress)
        {
            IPAddress address;

            if (request.RelayAgentIpAddress.Equals(IPAddress.Any))
            {
                //no relay agent
                if (request.ClientIpAddress.Equals(IPAddress.Any))
                {
                    address = interfaceAddress; //broadcast request
                }
                else
                {
                    if (!remoteAddress.Equals(request.ClientIpAddress))
                        return null; //client ip must match udp src addr

                    address = request.ClientIpAddress; //unicast request
                }
            }
            else
            {
                //relay agent unicast

                if (!remoteAddress.Equals(request.RelayAgentIpAddress))
                    return null; //relay ip must match udp src addr

                address = request.RelayAgentIpAddress;
            }

            foreach (KeyValuePair<string, Scope> scope in _scopes)
            {
                if (scope.Value.InterfaceAddress.Equals(interfaceAddress) && scope.Value.IsAddressInRange(address))
                    return scope.Value;
            }

            return null;
        }

        private void UpdateDnsAuthZone(bool add, Scope scope, Lease lease)
        {
            if (_authoritativeZoneRoot == null)
                return;

            if (string.IsNullOrEmpty(scope.DomainName))
                return;

            if (add)
            {
                //update forward zone
                if (!string.IsNullOrEmpty(scope.DomainName))
                {
                    if (!_authoritativeZoneRoot.ZoneExists(scope.DomainName))
                    {
                        //create forward zone
                        _authoritativeZoneRoot.SetRecords(scope.DomainName, DnsResourceRecordType.SOA, 14400, new DnsResourceRecordData[] { new DnsSOARecord(_authoritativeZoneRoot.ServerDomain, "hostmaster." + scope.DomainName, uint.Parse(DateTime.UtcNow.ToString("yyyyMMddHH")), 28800, 7200, 604800, 600) });
                        _authoritativeZoneRoot.SetRecords(scope.DomainName, DnsResourceRecordType.NS, 14400, new DnsResourceRecordData[] { new DnsNSRecord(_authoritativeZoneRoot.ServerDomain) });

                        _authoritativeZoneRoot.MakeZoneInternal(scope.DomainName);
                    }

                    _authoritativeZoneRoot.SetRecords(lease.HostName, DnsResourceRecordType.A, scope.DnsTtl, new DnsResourceRecordData[] { new DnsARecord(lease.Address) });
                }

                //update reverse zone
                {
                    if (!_authoritativeZoneRoot.ZoneExists(scope.ReverseZone))
                    {
                        //create reverse zone
                        _authoritativeZoneRoot.SetRecords(scope.ReverseZone, DnsResourceRecordType.SOA, 14400, new DnsResourceRecordData[] { new DnsSOARecord(_authoritativeZoneRoot.ServerDomain, "hostmaster." + scope.ReverseZone, uint.Parse(DateTime.UtcNow.ToString("yyyyMMddHH")), 28800, 7200, 604800, 600) });
                        _authoritativeZoneRoot.SetRecords(scope.ReverseZone, DnsResourceRecordType.NS, 14400, new DnsResourceRecordData[] { new DnsNSRecord(_authoritativeZoneRoot.ServerDomain) });

                        _authoritativeZoneRoot.MakeZoneInternal(scope.ReverseZone);
                    }

                    _authoritativeZoneRoot.SetRecords(Scope.GetReverseZone(lease.Address, 32), DnsResourceRecordType.PTR, scope.DnsTtl, new DnsResourceRecordData[] { new DnsPTRRecord(lease.HostName) });
                }
            }
            else
            {
                //remove from forward zone
                _authoritativeZoneRoot.DeleteRecords(lease.HostName, DnsResourceRecordType.A);

                //remove from reverse zone
                _authoritativeZoneRoot.DeleteRecords(Scope.GetReverseZone(lease.Address, 32), DnsResourceRecordType.PTR);
            }
        }

        private void BindUdpListener(IPEndPoint dhcpEP)
        {
            Socket udpListener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                #region this code ignores ICMP port unreachable responses which creates SocketException in ReceiveFrom()

                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    const uint IOC_IN = 0x80000000;
                    const uint IOC_VENDOR = 0x18000000;
                    const uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;

                    udpListener.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
                }

                #endregion

                //bind to interface address
                udpListener.EnableBroadcast = true;
                udpListener.Bind(dhcpEP);

                lock (_udpListeners)
                {
                    _udpListeners.Add(udpListener);
                }

                //start reading dhcp packets
                Thread listenerThread = new Thread(ReadUdpRequestAsync);
                listenerThread.IsBackground = true;
                listenerThread.Start(udpListener);

                lock (_listenerThreads)
                {
                    _listenerThreads.Add(listenerThread);
                }
            }
            catch
            {
                udpListener.Dispose();
                throw;
            }
        }

        private bool UnbindUdpListener(IPEndPoint dhcpEP)
        {
            lock (_udpListeners)
            {
                Socket foundSocket = null;

                foreach (Socket udpListener in _udpListeners)
                {
                    if (dhcpEP.Equals(udpListener.LocalEndPoint))
                    {
                        foundSocket = udpListener;
                        break;
                    }
                }

                if (foundSocket != null)
                {
                    foundSocket.Dispose();
                    return _udpListeners.Remove(foundSocket);
                }
            }

            return false;
        }

        private bool ActivateScope(Scope scope)
        {
            IPEndPoint dhcpEP = null;

            try
            {
                IPAddress interfaceAddress = scope.InterfaceAddress;
                dhcpEP = new IPEndPoint(interfaceAddress, 67);

                if (interfaceAddress.Equals(IPAddress.Any))
                {
                    if (_serverAnyAddressScopeCount < 1)
                        BindUdpListener(dhcpEP);

                    _serverAnyAddressScopeCount++;
                }
                else
                {
                    BindUdpListener(dhcpEP);
                }


                if (_authoritativeZoneRoot != null)
                {
                    //update valid leases into dns
                    DateTime utcNow = DateTime.UtcNow;

                    foreach (Lease lease in scope.Leases)
                    {
                        if (utcNow < lease.LeaseExpires)
                            UpdateDnsAuthZone(true, scope, lease); //lease valid
                    }
                }

                LogManager log = _log;
                if (log != null)
                    log.Write(dhcpEP, "DHCP Server successfully activated scope: " + scope.Name);

                return true;
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(dhcpEP, "DHCP Server failed to activate scope: " + scope.Name + "\r\n" + ex.ToString());
            }

            return false;
        }

        private bool DeactivateScope(Scope scope)
        {
            IPEndPoint dhcpEP = null;

            try
            {
                IPAddress interfaceAddress = scope.InterfaceAddress;
                dhcpEP = new IPEndPoint(interfaceAddress, 67);

                if (interfaceAddress.Equals(IPAddress.Any))
                {
                    if (_serverAnyAddressScopeCount < 2)
                    {
                        UnbindUdpListener(dhcpEP);
                        _serverAnyAddressScopeCount = 0;
                    }
                    else
                    {
                        _serverAnyAddressScopeCount--;
                    }
                }
                else
                {
                    UnbindUdpListener(dhcpEP);
                }

                if (_authoritativeZoneRoot != null)
                {
                    //remove all leases from dns
                    foreach (Lease lease in scope.Leases)
                        UpdateDnsAuthZone(false, scope, lease);
                }

                LogManager log = _log;
                if (log != null)
                    log.Write(dhcpEP, "DHCP Server successfully deactivated scope: " + scope.Name);

                return true;
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write(dhcpEP, "DHCP Server failed to deactivate scope: " + scope.Name + "\r\n" + ex.ToString());
            }

            return false;
        }

        private void LoadScope(Scope scope)
        {
            foreach (KeyValuePair<string, Scope> existingScope in _scopes)
            {
                if (existingScope.Value.Equals(scope))
                    throw new DhcpServerException("Scope with same range already exists.");
            }

            if (!_scopes.TryAdd(scope.Name, scope))
                throw new DhcpServerException("Scope with same name already exists.");

            if (scope.Enabled)
                ActivateScope(scope);

            LogManager log = _log;
            if (log != null)
                log.Write("DHCP Server successfully loaded scope: " + scope.Name);
        }

        private void UnloadScope(Scope scope)
        {
            DeactivateScope(scope);

            if (_scopes.TryRemove(scope.Name, out _))
            {
                LogManager log = _log;
                if (log != null)
                    log.Write("DHCP Server successfully unloaded scope: " + scope.Name);
            }
        }

        private void LoadAllScopeFiles()
        {
            string[] scopeFiles = Directory.GetFiles(_configFolder, "*.scope");

            foreach (string scopeFile in scopeFiles)
                LoadScopeFile(scopeFile);

            _lastModifiedScopesSavedOn = DateTime.UtcNow;
        }

        private void LoadScopeFile(string scopeFile)
        {
            try
            {
                using (FileStream fS = new FileStream(scopeFile, FileMode.Open, FileAccess.Read))
                {
                    LoadScope(new Scope(new BinaryReader(fS)));
                }

                LogManager log = _log;
                if (log != null)
                    log.Write("DHCP Server successfully loaded scope file: " + scopeFile);
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write("DHCP Server failed to load scope file: " + scopeFile + "\r\n" + ex.ToString());
            }
        }

        private void SaveScopeFile(Scope scope)
        {
            string scopeFile = Path.Combine(_configFolder, scope.Name + ".scope");

            try
            {
                using (FileStream fS = new FileStream(scopeFile, FileMode.Create, FileAccess.Write))
                {
                    scope.WriteTo(new BinaryWriter(fS));
                }

                LogManager log = _log;
                if (log != null)
                    log.Write("DHCP Server successfully saved scope file: " + scopeFile);
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write("DHCP Server failed to save scope file: " + scopeFile + "\r\n" + ex.ToString());
            }
        }

        private void DeleteScopeFile(Scope scope)
        {
            string scopeFile = Path.Combine(_configFolder, scope.Name + ".scope");

            try
            {
                File.Delete(scopeFile);

                LogManager log = _log;
                if (log != null)
                    log.Write("DHCP Server successfully deleted scope file: " + scopeFile);
            }
            catch (Exception ex)
            {
                LogManager log = _log;
                if (log != null)
                    log.Write("DHCP Server failed to delete scope file: " + scopeFile + "\r\n" + ex.ToString());
            }
        }

        private void SaveModifiedScopes()
        {
            DateTime currentDateTime = DateTime.UtcNow;

            foreach (KeyValuePair<string, Scope> scope in _scopes)
            {
                if (scope.Value.LastModified > _lastModifiedScopesSavedOn)
                    SaveScopeFile(scope.Value);
            }

            _lastModifiedScopesSavedOn = currentDateTime;
        }

        private void StartMaintenanceTimer()
        {
            if (_maintenanceTimer == null)
            {
                _maintenanceTimer = new Timer(delegate (object state)
                {
                    try
                    {
                        foreach (KeyValuePair<string, Scope> scope in _scopes)
                        {
                            scope.Value.RemoveExpiredOffers();

                            List<Lease> expiredLeases = scope.Value.RemoveExpiredLeases();

                            foreach (Lease expiredLease in expiredLeases)
                                UpdateDnsAuthZone(false, scope.Value, expiredLease);
                        }

                        SaveModifiedScopes();
                    }
                    catch (Exception ex)
                    {
                        LogManager log = _log;
                        if (log != null)
                            log.Write(ex);
                    }
                    finally
                    {
                        if (!_disposed)
                            _maintenanceTimer.Change(MAINTENANCE_TIMER_INTERVAL, Timeout.Infinite);
                    }
                }, null, Timeout.Infinite, Timeout.Infinite);
            }

            _maintenanceTimer.Change(MAINTENANCE_TIMER_INTERVAL, Timeout.Infinite);
        }

        private void StopMaintenanceTimer()
        {
            _maintenanceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        #endregion

        #region public

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException("DhcpServer");

            if (_state != ServiceState.Stopped)
                throw new InvalidOperationException("DHCP Server is already running.");

            _state = ServiceState.Starting;

            LoadAllScopeFiles();
            StartMaintenanceTimer();

            _state = ServiceState.Running;
        }

        public void Stop()
        {
            if (_state != ServiceState.Running)
                return;

            _state = ServiceState.Stopping;

            StopMaintenanceTimer();

            foreach (KeyValuePair<string, Scope> scope in _scopes)
                UnloadScope(scope.Value);

            _listenerThreads.Clear();
            _udpListeners.Clear();

            _state = ServiceState.Stopped;
        }

        public void AddScope(Scope scope)
        {
            LoadScope(scope);
            SaveScopeFile(scope);
        }

        public Scope GetScope(string name)
        {
            if (_scopes.TryGetValue(name, out Scope scope))
                return scope;

            return null;
        }

        public void RenameScope(string name, string newName)
        {
            if (_scopes.TryGetValue(name, out Scope scope))
                throw new DhcpServerException("Scope with name '" + name + "' does not exists.");

            if (!_scopes.TryAdd(newName, scope))
                throw new DhcpServerException("Scope with name '" + newName + "' already exists.");

            scope.Name = newName;
            _scopes.TryRemove(name, out _);
        }

        public void DeleteScope(string name)
        {
            if (_scopes.TryGetValue(name, out Scope scope))
            {
                UnloadScope(scope);
                DeleteScopeFile(scope);
            }
        }

        public void EnableScope(string name)
        {
            if (_scopes.TryGetValue(name, out Scope scope))
            {
                if (ActivateScope(scope))
                {
                    scope.SetEnabled(true);
                    SaveScopeFile(scope);
                }
            }
        }

        public void DisableScope(string name)
        {
            if (_scopes.TryGetValue(name, out Scope scope))
            {
                if (DeactivateScope(scope))
                {
                    scope.SetEnabled(false);
                    SaveScopeFile(scope);
                }
            }
        }

        public IDictionary<string, string> GetAddressClientMap()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();

            foreach (KeyValuePair<string, Scope> scope in _scopes)
            {
                foreach (Lease lease in scope.Value.Leases)
                {
                    if (!string.IsNullOrEmpty(lease.HostName))
                        map.Add(lease.Address.ToString(), lease.HostName);
                }
            }

            return map;
        }

        #endregion

        #region properties

        public ICollection<Scope> Scopes
        { get { return _scopes.Values; } }

        public Zone AuthoritativeZoneRoot
        {
            get { return _authoritativeZoneRoot; }
            set { _authoritativeZoneRoot = value; }
        }

        public LogManager LogManager
        {
            get { return _log; }
            set { _log = value; }
        }

        #endregion
    }
}