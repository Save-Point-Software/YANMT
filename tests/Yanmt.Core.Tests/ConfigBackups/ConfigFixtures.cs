namespace Yanmt.Core.Tests.ConfigBackups;

/// <summary>Invented configs only: made-up hostnames, RFC 5737 addresses, obviously fake secrets.</summary>
internal static class ConfigFixtures
{
    public const string Router = """
        Building configuration...

        Current configuration : 4821 bytes
        !
        ! Last configuration change at 10:15:42 UTC Mon Jan 5 2026 by labadmin
        ! NVRAM config last updated at 10:16:01 UTC Mon Jan 5 2026 by labadmin
        !
        version 17.9
        hostname lab-rtr-01
        !
        enable secret 9 $9$FAKEFAKEFAKE$notarealhashnotarealhashnotarealhash00
        username labadmin privilege 15 secret 9 $9$FAKEFAKE$anotherfakehashvalue000000000000000000000
        username oldadmin password 7 0000FAKE0000
        !
        key chain LAB-KEYS
         key 1
          key-string 7 0000FAKEKEY00
        !
        interface GigabitEthernet1
         description uplink to lab-sw-01
         ip address 192.0.2.1 255.255.255.0
        !
        ntp clock-period 17179869
        ntp server 198.51.100.10
        snmp-server community FakeCommunity RO
        snmp-server user labmon labgrp v3 auth sha FakeAuthPass123 priv aes 128 FakePrivPass456
        tacacs-server host 203.0.113.5 key 7 0000FAKETAC00
        crypto key generate rsa modulus 2048
        !
        line vty 0 4
         password 0 FakeVtyPassword
         login local
        !
        end
        """;
}
