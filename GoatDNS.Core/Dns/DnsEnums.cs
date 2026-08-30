namespace GoatDNS.Core.Dns;

public enum DnsRecordType : ushort
{
    A = 1,
    NS = 2,
    CNAME = 5,
    SOA = 6,
    PTR = 12,
    MX = 15,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    OPT = 41,
    DS = 43,
    RRSIG = 46,
    NSEC = 47,
    DNSKEY = 48,
    NSEC3 = 50,
    SVCB = 64,
    HTTPS = 65,
    ANY = 255,
}

public enum DnsResponseCode : byte
{
    NoError = 0,
    FormErr = 1,
    ServFail = 2,
    NxDomain = 3,
    NotImp = 4,
    Refused = 5,
}

public enum DnsOpcode : byte
{
    Query = 0,
    Notify = 4,
    Update = 5,
}
