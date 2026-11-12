namespace Rrf5.Fiscal.Nfce;

/// <summary>Issuer address as the layout writes it (group enderEmit).</summary>
public sealed record NfceAddress(string Street, string Number, string District, string MunicipalityCode, string Municipality, string Uf, string Zip);

public sealed record NfceIssuer(string Cnpj, string StateRegistration, string Name, NfceAddress Address, string Crt);

/// <summary>Identification (group ide). <paramref name="Environment"/> is tpAmb: "1" production, "2" homologation.</summary>
public sealed record NfceIdentification(
    string Cuf, string Cnf, string NatOp, int Series, long Number, DateTimeOffset IssuedAt, string Environment, string MunicipalityCode, string VerProc);

/// <summary>Consumer identified by CPF (group dest; NFC-e uses indIEDest 9, Anexo I field E16a, Note 1).</summary>
public sealed record NfceConsumer(string Cpf, string? Name);

/// <summary>Taxes of one item, exactly as the approved rule version gives them (generator nfce-4.00-g1 groups only).</summary>
public sealed record NfceTaxes(string Orig, string Csosn, string PisCst, decimal PisRatePercent, string CofinsCst, decimal CofinsRatePercent);

public sealed record NfceItem(
    string ProductCode, string Gtin, string Description, string Ncm, string Cfop, string Unit,
    decimal Quantity, decimal UnitPrice, decimal LineValue, decimal Discount, decimal Surcharge, NfceTaxes Taxes);

public sealed record NfceCard(int Integration, string? AcquirerCnpj, string? Brand, string? AuthorizationCode);

public sealed record NfcePayment(string TPag, string? Description, decimal Amount, NfceCard? Card);

/// <summary>Everything the generator writes. All values are already resolved; the generator decides nothing fiscal.</summary>
public sealed record NfceDocument(
    NfceIdentification Identification,
    NfceIssuer Issuer,
    NfceConsumer? Consumer,
    IReadOnlyList<NfceItem> Items,
    IReadOnlyList<NfcePayment> Payments,
    decimal Change,
    string QrCodeUrl,
    string ConsultationUrl);

public sealed record GeneratedNfce(string Xml, string AccessKey, decimal Total);
