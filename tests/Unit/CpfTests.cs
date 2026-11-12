using Rrf5.Fiscal.Domain.Issuer;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>MOC 7.0 Anexo I rule E05-10 (rejection 237): zeros, repeated digits or a wrong check digit are refused.</summary>
public sealed class CpfTests
{
    [Theory]
    [InlineData("12345678909", true)]
    [InlineData("12345678900", false)]
    [InlineData("00000000000", false)]
    [InlineData("11111111111", false)]
    [InlineData("99999999999", false)]
    [InlineData("1234567890", false)]
    [InlineData("123456789O9", false)]
    [InlineData(null, false)]
    public void ChecksTheOfficialRule(string? cpf, bool valid) => Assert.Equal(valid, Cpf.IsValid(cpf));
}
