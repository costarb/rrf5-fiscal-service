using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>T023: A1 inspection with throwaway "NOT AN A1" certificates.</summary>
public sealed class CertificateInspectionTests
{
    private const string Password = "not-a-real-password";
    private const string Holder = "12ABC34501DE35";

    [Fact]
    public void ReadsHolderValidityAndFingerprintFromTheIcpBrasilOtherName()
    {
        var facts = CertificateInspection.Inspect(TestCertificates.Pkcs12(Password, Holder), Password, DateTimeOffset.UtcNow);
        Assert.Equal(Holder, facts.HolderTaxId.Value);
        Assert.Matches("^[a-f0-9]{64}$", facts.FingerprintSha256);
        Assert.True(facts.NotAfter > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void FallsBackToTheSubjectSerialNumber()
    {
        var facts = CertificateInspection.Inspect(TestCertificates.Pkcs12(Password, Holder, cnpjInSerialNumberOnly: true), Password, DateTimeOffset.UtcNow);
        Assert.Equal(Holder, facts.HolderTaxId.Value);
    }

    [Fact]
    public void WrongPasswordFailsWithoutEchoingIt()
    {
        var exception = Assert.Throws<DomainException>(() =>
            CertificateInspection.Inspect(TestCertificates.Pkcs12(Password, Holder), "CANARY-WRONG-PASSWORD", DateTimeOffset.UtcNow));
        Assert.Equal(ErrorCodes.CertificateInvalid, exception.Code);
        Assert.DoesNotContain("CANARY", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-30, -1)]   // expired
    [InlineData(1, 30)]     // not yet valid
    public void OutsideValidityIsRefused(int fromDays, int toDays)
    {
        var pkcs12 = TestCertificates.Pkcs12(Password, Holder, DateTimeOffset.UtcNow.AddDays(fromDays), DateTimeOffset.UtcNow.AddDays(toDays));
        Assert.Equal(ErrorCodes.CertificateExpired,
            Assert.Throws<DomainException>(() => CertificateInspection.Inspect(pkcs12, Password, DateTimeOffset.UtcNow)).Code);
    }

    [Fact]
    public void CertificateWithoutHolderKeyOrRsaIsRefused()
    {
        foreach (var pkcs12 in new[]
        {
            TestCertificates.Pkcs12(Password, holderCnpj: null),
            TestCertificates.Pkcs12(Password, Holder, withoutPrivateKey: true),
            TestCertificates.Pkcs12(Password, Holder, ecdsa: true),
            TestCertificates.Pkcs12(Password, "12ABC34501DE36"),   // holder with a wrong check digit
        })
        {
            Assert.Equal(ErrorCodes.CertificateInvalid,
                Assert.Throws<DomainException>(() => CertificateInspection.Inspect(pkcs12, Password, DateTimeOffset.UtcNow)).Code);
        }
    }

    [Fact]
    public void HolderRootMustMatchTheEstablishmentRoot()
    {
        var facts = CertificateInspection.Inspect(TestCertificates.Pkcs12(Password, Holder), Password, DateTimeOffset.UtcNow);
        CertificateInspection.RequireSameRoot(facts, "12ABC345000250");   // branch of the same company: accepted (MOC rule F03)
        Assert.Equal(ErrorCodes.CertificateHolderMismatch,
            Assert.Throws<DomainException>(() => CertificateInspection.RequireSameRoot(facts, "11222333000181")).Code);
    }

    [Fact]
    public void OversizedFileIsRefusedBeforeParsing() =>
        Assert.Equal(ErrorCodes.CertificateInvalid,
            Assert.Throws<DomainException>(() => CertificateInspection.Inspect(new byte[CertificateInspection.MaxPkcs12Bytes + 1], Password, DateTimeOffset.UtcNow)).Code);
}
