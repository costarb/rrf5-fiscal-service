using System.Globalization;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Application.Issuer;
using Rrf5.Fiscal.Domain.Documents;
using Rrf5.Fiscal.Domain.Issuer;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Documents;

public sealed record ConsumerInput(string? Cpf, string? Name);

public sealed record CardInput(int? Integration, string? AcquirerCnpj, string? Brand, string? AuthorizationCode);

public sealed record ItemInput(
    string? ProductCode, string? Description, Guid? FiscalProfileId, string? ItemClass, string? Unit,
    string? Quantity, string? UnitPrice, string? Discount, string? Surcharge);

public sealed record PaymentInput(string? Method, string? Amount, string? Description, CardInput? Card);

/// <summary>The sale as the client sends it (contract <c>CreateDocument</c>). Decimals arrive as strings.</summary>
public sealed record DocumentInput(
    string? ExternalId, Guid? SeriesId, string? Operation, ConsumerInput? Consumer,
    IReadOnlyList<ItemInput>? Items, IReadOnlyList<PaymentInput>? Payments, string? Change);

public sealed record DocumentProblem(string Code, string Detail, string? JsonPointer);

public sealed record ResolvedItem(int Number, ItemInput Input, decimal Quantity, decimal UnitPrice, decimal LineValue, decimal Discount, decimal Surcharge, TaxRules.ResolvedRule? Rule);

public sealed record ResolvedPayment(int Number, PaymentInput Input, decimal Amount, string? TPag);

public sealed record DocumentTotals(decimal Products, decimal Discounts, decimal Surcharges, decimal Total, decimal Payments, decimal Change);

/// <summary>Everything the emission needs, or the list of problems that block it.</summary>
public sealed record PrevalidationReport(
    IReadOnlyList<DocumentProblem> Problems,
    DocumentTotals? Totals,
    IReadOnlyList<ResolvedItem> Items,
    IReadOnlyList<ResolvedPayment> Payments,
    SeriesRecord? Series,
    Certificates.CertificateRecord? Certificate,
    SchemaPackage? Schema)
{
    public bool Valid => Problems.Count == 0;
}

/// <summary>
/// 005 T010 and T011 (E2): checks a sale without reserving a number and without
/// calling SEFAZ. Every problem is collected, with a JSON pointer into the request.
/// Tax treatment comes only from approved rule versions in force on the issue date;
/// nothing is inferred (FR-017, ADR-015). Payment rules cite MOC 7.0 Anexo I, group YA.
/// </summary>
public sealed class DocumentPrevalidation(IUnitOfWorkFactory database, PaymentMethods paymentTable, TimeProvider clock)
{
    public const string Model = "65";
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public async Task<PrevalidationReport> PrevalidateAsync(Caller caller, Guid environmentId, DocumentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.DocumentsIssue);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        return await CheckAsync(uow, caller, environmentId, input, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The checks, inside the caller's unit of work (also used by the issue path).</summary>
    public async Task<PrevalidationReport> CheckAsync(IUnitOfWork uow, Caller caller, Guid environmentId, DocumentInput input, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(input);
        RequireShape(input);
        var problems = new List<DocumentProblem>();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(issuedAt, SaoPaulo).DateTime);

        var environment = await uow.Issuer.GetEnvironmentAsync(environmentId, cancellationToken).ConfigureAwait(false);
        if (environment is null
            || !(await uow.Identity.ListEstablishmentGrantsAsync(caller.ApplicationId, cancellationToken).ConfigureAwait(false)).Contains(environment.EstablishmentId))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        var establishment = (await uow.Issuer.GetEstablishmentAsync(environment.EstablishmentId, cancellationToken).ConfigureAwait(false))!;
        if (establishment.Status != "ACTIVE" || environment.Status != "ACTIVE")
        {
            problems.Add(Problem(ErrorCodes.ConstraintViolated, "The establishment or the environment is not active.", null));
        }

        var configuration = await uow.Issuer.GetActiveConfigurationAsync(environment.Id, cancellationToken).ConfigureAwait(false);
        SchemaPackage? schema = null;
        if (configuration is null)
        {
            problems.Add(Problem(ErrorCodes.ConstraintViolated, "The environment has no active configuration.", null));
        }
        else
        {
            var authorizer = Authorizers.ByUf.Values.FirstOrDefault(a => a.Code == configuration.Authorizer);
            schema = authorizer is null
                ? null
                : SchemaResolver.Resolve(await uow.Schemas.ListAsync(cancellationToken).ConfigureAwait(false), Model, authorizer.Uf, environment.Environment, date);
            if (schema is null)
            {
                problems.Add(Problem(ErrorCodes.SchemaNotResolved, "No catalogued schema package applies on the issue date.", null));
            }
        }

        var series = await ResolveSeriesAsync(uow, environment.Id, input.SeriesId, problems, cancellationToken).ConfigureAwait(false);
        var certificate = (await uow.Certificates.ListAsync(environment.Id, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => c.Status == "ACTIVE" && c.NotBefore <= issuedAt && issuedAt <= c.NotAfter);
        if (certificate is null)
        {
            problems.Add(Problem(ErrorCodes.CertificateNotActive, "No ACTIVE certificate is within its validity on the issue date.", null));
        }

        var items = await ResolveItemsAsync(uow, establishment, input, date, problems, cancellationToken).ConfigureAwait(false);
        var payments = await ResolvePaymentsAsync(uow, environment.Id, input, date, problems, cancellationToken).ConfigureAwait(false);
        var totals = CheckTotals(input, items, payments, problems);

        if (input.Consumer?.Cpf is { } cpf && !Cpf.IsValid(cpf))
        {
            problems.Add(Problem(ErrorCodes.ConsumerInvalid, "The consumer CPF is invalid (MOC rule E05-10, rejection 237).", "/consumer/cpf"));
        }

        return new PrevalidationReport(problems, totals, items, payments, series, certificate, schema);
    }

    private static async Task<SeriesRecord?> ResolveSeriesAsync(IUnitOfWork uow, Guid environmentId, Guid? seriesId, List<DocumentProblem> problems, CancellationToken cancellationToken)
    {
        var candidates = (await uow.Issuer.ListSeriesAsync(environmentId, cancellationToken).ConfigureAwait(false))
            .Where(s => s.Model == Model && s.Status == "ACTIVE" && s.NextNumber <= s.NumberMax)
            .ToList();
        var series = seriesId is { } id
            ? candidates.FirstOrDefault(s => s.Id == id)
            : candidates.Count == 1 ? candidates[0] : null;
        if (series is null)
        {
            problems.Add(Problem(ErrorCodes.SeriesNotAvailable, seriesId is null && candidates.Count > 1
                ? "The environment has more than one active NFC-e series; inform seriesId."
                : "No active NFC-e series with numbers left in this environment.", "/seriesId"));
        }

        return series;
    }

    private static async Task<List<ResolvedItem>> ResolveItemsAsync(
        IUnitOfWork uow, EstablishmentRecord establishment, DocumentInput input, DateOnly date, List<DocumentProblem> problems, CancellationToken cancellationToken)
    {
        var items = new List<ResolvedItem>();
        for (var i = 0; i < input.Items!.Count; i++)
        {
            var item = input.Items[i];
            var pointer = $"/items/{i.ToString(CultureInfo.InvariantCulture)}";
            var quantity = Decimal(item.Quantity, pointer + "/quantity");
            var unitPrice = Decimal(item.UnitPrice, pointer + "/unitPrice");
            var discount = Decimal(item.Discount ?? "0", pointer + "/discount");
            var surcharge = Decimal(item.Surcharge ?? "0", pointer + "/surcharge");
            if (quantity <= 0)
            {
                throw new DomainException(ErrorCodes.RequestInvalid, "Item quantity must be greater than zero.");
            }

            var lineValue = FiscalRounding.LineValue(quantity, unitPrice);
            if (discount > lineValue)
            {
                problems.Add(Problem(ErrorCodes.TotalsMismatch, "The item discount is greater than the item value.", pointer + "/discount"));
            }

            TaxRules.ResolvedRule? rule = null;
            try
            {
                rule = await TaxRules.TaxRuleAdministration.ResolveInForceAsync(
                    uow, establishment, item.FiscalProfileId!.Value, Model, input.Operation, item.ItemClass, date, cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException exception) when (exception.Code == ErrorCodes.RuleNotInForce || exception.Code == ErrorCodes.RuleCrtMismatch || exception.Code == ErrorCodes.NotFound)
            {
                problems.Add(Problem(exception.Code == ErrorCodes.NotFound ? ErrorCodes.RuleNotInForce : exception.Code,
                    "No approved tax rule of the profile covers this item on the issue date.", pointer));
            }

            items.Add(new ResolvedItem(i + 1, item, quantity, unitPrice, lineValue, discount, surcharge, rule));
        }

        return items;
    }

    private async Task<List<ResolvedPayment>> ResolvePaymentsAsync(
        IUnitOfWork uow, Guid environmentId, DocumentInput input, DateOnly date, List<DocumentProblem> problems, CancellationToken cancellationToken)
    {
        var mapping = (await uow.PaymentMappings.ListActiveAsync(environmentId, cancellationToken).ConfigureAwait(false))
            .ToDictionary(m => m.CommercialMethod, m => m.TPag, StringComparer.Ordinal);
        var payments = new List<ResolvedPayment>();
        for (var i = 0; i < input.Payments!.Count; i++)
        {
            var payment = input.Payments[i];
            var pointer = $"/payments/{i.ToString(CultureInfo.InvariantCulture)}";
            if (payment.Method is null || !CommercialPaymentMethods.All.Contains(payment.Method))
            {
                throw new DomainException(ErrorCodes.RequestInvalid, "A payment method is not in the catalog.");
            }

            var amount = Money(payment.Amount, pointer + "/amount");
            string? tPag = null;
            if (!mapping.TryGetValue(payment.Method, out var code))
            {
                problems.Add(Problem(ErrorCodes.PaymentMethodNotConfigured, "The environment does not map this commercial method to a tPag code.", pointer + "/method"));
            }
            else if (paymentTable.Resolve(code, date) is null)
            {
                problems.Add(Problem(ErrorCodes.PaymentMethodNotInForce, "The mapped tPag code is not in force on the issue date.", pointer + "/method"));
            }
            else
            {
                tPag = code;
                CheckPaymentDetails(payment, tPag, amount, pointer, problems);
            }

            payments.Add(new ResolvedPayment(i + 1, payment, amount, tPag));
        }

        return payments;
    }

    // MOC 7.0 Anexo I, group YA: 904 (YA04-10), 391 (YA04a-20), 392 (YA05-10), 437 (YA05-20).
    private static void CheckPaymentDetails(PaymentInput payment, string tPag, decimal amount, string pointer, List<DocumentProblem> problems)
    {
        if (tPag == "90" && amount != 0)
        {
            problems.Add(Problem(ErrorCodes.TotalsMismatch, "tPag 90 (sem pagamento) requires a zero amount (MOC rejection 904).", pointer + "/amount"));
        }

        if (tPag is "03" or "04" && payment.Card is null)
        {
            problems.Add(Problem(ErrorCodes.PaymentCardRequired, "Card payments (tPag 03, 04) carry the card group (MOC rejection 391).", pointer + "/card"));
        }

        if (payment.Card is { } card)
        {
            if (card.Integration is not (1 or 2))
            {
                problems.Add(Problem(ErrorCodes.PaymentCardRequired, "card.integration must be 1 or 2 (tpIntegra).", pointer + "/card/integration"));
            }
            else if (card.Integration == 1 && (string.IsNullOrEmpty(card.AcquirerCnpj) || string.IsNullOrEmpty(card.AuthorizationCode)))
            {
                problems.Add(Problem(ErrorCodes.PaymentCardRequired, "An integrated card payment carries the acquirer CNPJ and the authorization code (MOC rejection 392).", pointer + "/card"));
            }

            if (card.AcquirerCnpj is { } cnpj && !Cnpj.TryParse(cnpj, out _))
            {
                problems.Add(Problem(ErrorCodes.PaymentCardRequired, "The payment institution CNPJ is invalid (MOC rejection 437).", pointer + "/card/acquirerCnpj"));
            }
        }
    }

    // vNF per rule W16 for the components this generator builds (vProd - vDesc + vOutro);
    // payments per YA03-10 (865), YA03-20 (866) and YA09-10 (869).
    private static DocumentTotals CheckTotals(DocumentInput input, List<ResolvedItem> items, List<ResolvedPayment> payments, List<DocumentProblem> problems)
    {
        var products = items.Sum(i => i.LineValue);
        var discounts = items.Sum(i => i.Discount);
        var surcharges = items.Sum(i => i.Surcharge);
        var total = products - discounts + surcharges;
        var paid = payments.Sum(p => p.Amount);
        var change = Money(input.Change ?? "0.00", "/change");

        if (paid < total)
        {
            problems.Add(Problem(ErrorCodes.TotalsMismatch, "The payments are less than the document total (MOC rejection 865).", "/payments"));
        }
        else if (paid > total && change == 0)
        {
            problems.Add(Problem(ErrorCodes.TotalsMismatch, "The payments exceed the total and no change is informed (MOC rejection 866).", "/change"));
        }
        else if (change != 0 && change != paid - total)
        {
            problems.Add(Problem(ErrorCodes.TotalsMismatch, "The change differs from payments minus total (MOC rejection 869).", "/change"));
        }

        return new DocumentTotals(products, discounts, surcharges, total, paid, change);
    }

    private static void RequireShape(DocumentInput input)
    {
        if (string.IsNullOrWhiteSpace(input.ExternalId) || input.ExternalId.Length > 100)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "externalId is required (1 to 100 characters).");
        }

        if (string.IsNullOrWhiteSpace(input.Operation) || input.Operation.Length > 60)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "operation is required (1 to 60 characters).");
        }

        if (input.Items is not { Count: >= 1 and <= 990 })
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "items must have 1 to 990 entries.");
        }

        if (input.Payments is not { Count: >= 1 and <= 100 })
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "payments must have 1 to 100 entries.");
        }

        if (input.Items.Any(i => i.FiscalProfileId is null || string.IsNullOrWhiteSpace(i.ItemClass) || string.IsNullOrWhiteSpace(i.ProductCode)
            || string.IsNullOrWhiteSpace(i.Description) || string.IsNullOrWhiteSpace(i.Unit)))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "Every item needs productCode, description, fiscalProfileId, itemClass and unit.");
        }
    }

    private static decimal Decimal(string? value, string pointer)
    {
        if (value is null || value.Length > 24 || value.StartsWith('-')
            || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, $"{pointer} must be a non-negative decimal with a dot separator.");
        }

        return parsed;
    }

    private static decimal Money(string? value, string pointer)
    {
        var parsed = Decimal(value, pointer);
        if (parsed != FiscalRounding.Money(parsed))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, $"{pointer} must have at most two decimal places.");
        }

        return parsed;
    }

    private static DocumentProblem Problem(ErrorCode code, string detail, string? pointer) => new(code.Value, detail, pointer);
}
