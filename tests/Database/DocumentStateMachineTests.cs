using Npgsql;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>
/// 0011 (005 T007, T008): the database enforces the closed document state machine.
/// <list type="bullet">
/// <item><c>AUTHORIZED</c> needs a cStat 100 protocol for the same key and digest, plus the stored nfeProc.</item>
/// <item>A resend after sending needs a later consultation that answered 217.</item>
/// <item>Emission records are append-only.</item>
/// </list>
/// Synthetic values only.
/// </summary>
[Collection(DatabaseGroup.Name)]
public sealed class DocumentStateMachineTests(DatabaseFixture db)
{
    /// <summary>A syntactically valid SHA-1 DigestValue (synthetic).</summary>
    public const string DigVal = "AAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string OtherDigVal = "BBBBBBBBBBBBBBBBBBBBBBBBBBB=";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] Path = ["VALIDATING", "READY_TO_SIGN", "SIGNING", "SIGNED", "QUEUED", "SENDING", "WAITING_SEFAZ"];

    [Fact]
    public async Task OnlyTheEdgesOfTheStateMachineExist()
    {
        await using var owner = await db.OpenOwnerAsync();
        var (issuer, document, _) = await NewDocumentAsync(owner);

        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => SetStateAsync(owner, document, "SIGNED"));
        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => SetStateAsync(owner, document, "AUTHORIZED"));
        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => InsertDocumentAtAsync(owner, issuer, "AUTHORIZED"));

        await WalkAsync(owner, document, "QUEUED", storeSentXml: false);
        await Sql.FailsWithAsync(SqlState.Consistency, () => SetStateAsync(owner, document, "SENDING"));
        await Sql.ExecuteAsync(owner, "update fiscal_document set sent_xml_object_key = 'documents/x', sent_xml_object_version = 'v1' where id = $1", document);
        await SetStateAsync(owner, document, "SENDING");

        // After a request leaves, doubt is WAITING_SEFAZ, never ERROR.
        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => SetStateAsync(owner, document, "ERROR"));
        await Sql.FailsWithAsync(SqlState.ImmutableField, () => Sql.ExecuteAsync(owner,
            "update fiscal_document set sent_xml_object_key = 'documents/y' where id = $1", document));
    }

    [Fact]
    public async Task ErrorReturnsOnlyToTheStageBeforeTheRequestLeaves()
    {
        await using var owner = await db.OpenOwnerAsync();
        var (_, unsigned, _) = await NewDocumentAsync(owner);
        await WalkAsync(owner, unsigned, "READY_TO_SIGN");
        await SetStateAsync(owner, unsigned, "ERROR");
        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => SetStateAsync(owner, unsigned, "QUEUED"));
        await SetStateAsync(owner, unsigned, "READY_TO_SIGN");

        var (_, signed, _) = await NewDocumentAsync(owner);
        await WalkAsync(owner, signed, "SIGNED");
        await SetStateAsync(owner, signed, "ERROR");
        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => SetStateAsync(owner, signed, "READY_TO_SIGN"));
        await SetStateAsync(owner, signed, "QUEUED");
    }

    [Fact]
    public async Task AuthorizedNeedsAProtocolForThisKeyAndDigestAndTheStoredProc()
    {
        await using var owner = await db.OpenOwnerAsync();
        var (issuer, document, key) = await NewDocumentAsync(owner);
        await WalkAsync(owner, document, "WAITING_SEFAZ");
        var exchange = await ExchangeAsync(owner, issuer.TenantId, document, "AUTHORIZE", 100, DateTimeOffset.UtcNow);

        // A cStat 100 for another digest (another document's protocol) is not an authorization.
        await ProtocolAsync(owner, issuer.TenantId, document, exchange, 100, key, OtherDigVal);
        await Sql.FailsWithAsync(SqlState.Consistency, () => SetStateAsync(owner, document, "AUTHORIZED"));

        await ProtocolAsync(owner, issuer.TenantId, document, exchange, 100, key, DigVal);
        await Sql.FailsWithAsync(SqlState.Consistency, () => SetStateAsync(owner, document, "AUTHORIZED"));

        await Sql.ExecuteAsync(owner,
            "update fiscal_document set proc_xml_sha256 = $2, proc_xml_object_key = 'documents/p', proc_xml_object_version = 'v1', authorized_at = now() where id = $1",
            document, Hash);
        await SetStateAsync(owner, document, "AUTHORIZED");

        // The nfeProc reference and the protocol never change.
        await Sql.FailsWithAsync(SqlState.ImmutableField, () => Sql.ExecuteAsync(owner,
            "update fiscal_document set proc_xml_object_key = 'documents/q' where id = $1", document));
        await Sql.FailsWithAsync(SqlState.AppendOnly, () => Sql.ExecuteAsync(owner,
            "update fiscal_protocol set c_stat = 101 where document_id = $1", document));
        await Sql.FailsWithAsync(SqlState.AppendOnly, () => Sql.ExecuteAsync(owner,
            "delete from sefaz_exchange where document_id = $1", document));
    }

    [Fact]
    public async Task RejectedNeedsTheOfficialAnswerAndIsTerminal()
    {
        await using var owner = await db.OpenOwnerAsync();
        var (issuer, document, _) = await NewDocumentAsync(owner);
        await WalkAsync(owner, document, "SENDING");
        await Sql.FailsWithAsync(SqlState.Consistency, () => SetStateAsync(owner, document, "REJECTED"));

        var exchange = await ExchangeAsync(owner, issuer.TenantId, document, "AUTHORIZE", 225, DateTimeOffset.UtcNow);
        await Sql.ExecuteAsync(owner,
            """
            insert into fiscal_protocol (id, tenant_id, document_id, exchange_id, kind, c_stat, x_motivo, received_at)
            values ($1, $2, $3, $4, 'BATCH', 225, 'Rejeicao: Falha no Schema XML do lote de NFe', now())
            """,
            Guid.NewGuid(), issuer.TenantId, document, exchange);
        await SetStateAsync(owner, document, "REJECTED");
        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => SetStateAsync(owner, document, "QUEUED"));
    }

    [Fact]
    public async Task AResendAfterSendingNeedsALaterConsultationThatAnswered217()
    {
        await using var owner = await db.OpenOwnerAsync();
        var (issuer, document, _) = await NewDocumentAsync(owner);
        await WalkAsync(owner, document, "WAITING_SEFAZ");
        var sentAt = DateTimeOffset.UtcNow;
        await ExchangeAsync(owner, issuer.TenantId, document, "AUTHORIZE", null, sentAt, outcome: "TIMEOUT");

        await Sql.FailsWithAsync(SqlState.Consistency, () => SetStateAsync(owner, document, "QUEUED"));

        // A consultation before the last send does not count, nor does one with another answer.
        await ExchangeAsync(owner, issuer.TenantId, document, "CONSULT", 217, sentAt.AddMinutes(-5));
        await ExchangeAsync(owner, issuer.TenantId, document, "CONSULT", 100, sentAt.AddMinutes(1));
        await Sql.FailsWithAsync(SqlState.Consistency, () => SetStateAsync(owner, document, "QUEUED"));

        await ExchangeAsync(owner, issuer.TenantId, document, "CONSULT", 217, sentAt.AddMinutes(2));
        await SetStateAsync(owner, document, "QUEUED");
    }

    [Fact]
    public async Task RolesGetOnlyTheirPartOfTheEmissionRecords()
    {
        await using var owner = await db.OpenOwnerAsync();
        var (issuer, document, _) = await NewDocumentAsync(owner);
        await WalkAsync(owner, document, "SENDING");

        // The API cannot record SEFAZ answers or move documents; the worker can, inside its tenant.
        await using (var app = await db.BeginAsAppAsync(issuer.TenantId))
        {
            await app.FailsInSavepointAsync(SqlState.InsufficientPrivilege,
                "insert into sefaz_exchange (id, tenant_id, document_id, operation, outcome, request_sha256, started_at, finished_at) values ($1, $2, $3, 'AUTHORIZE', 'NOT_SENT', $4, now(), now())",
                Guid.NewGuid(), issuer.TenantId, document, Hash);
            Assert.Equal(1, await app.ExecuteAsync(
                "insert into document_outbox (id, tenant_id, document_id, kind) values ($1, $2, $3, 'PROCESS')", Guid.NewGuid(), issuer.TenantId, document));
        }

        await using (var worker = await db.BeginAsAsync("rrf5_worker", issuer.TenantId))
        {
            Assert.Equal(1, await worker.ExecuteAsync(
                "insert into sefaz_exchange (id, tenant_id, document_id, operation, outcome, request_sha256, started_at, finished_at) values ($1, $2, $3, 'AUTHORIZE', 'TIMEOUT', $4, now(), now())",
                Guid.NewGuid(), issuer.TenantId, document, Hash));
            Assert.Equal(1, await worker.ExecuteAsync("update fiscal_document set state = 'WAITING_SEFAZ' where id = $1", document));
        }

        // Another tenant's worker context sees nothing.
        await using (var foreign = await db.BeginAsAsync("rrf5_worker", Guid.NewGuid()))
        {
            Assert.Equal(0, await foreign.ExecuteAsync("update fiscal_document set state = 'QUEUED' where id = $1", document));
        }
    }

    [Fact]
    public async Task OutboxIsPublishedOnceAndAttemptsNeverDecrease()
    {
        await using var owner = await db.OpenOwnerAsync();
        var (issuer, document, _) = await NewDocumentAsync(owner);
        var message = Guid.NewGuid();
        await Sql.ExecuteAsync(owner, "insert into document_outbox (id, tenant_id, document_id, kind) values ($1, $2, $3, 'PROCESS')", message, issuer.TenantId, document);
        await Sql.ExecuteAsync(owner, "update document_outbox set attempts = 2, published_at = now() where id = $1", message);
        await Sql.FailsWithAsync(SqlState.ImmutableField, () => Sql.ExecuteAsync(owner, "update document_outbox set published_at = now() + interval '1 hour' where id = $1", message));
        await Sql.FailsWithAsync(SqlState.MonotonicDecrease, () => Sql.ExecuteAsync(owner, "update document_outbox set attempts = 1 where id = $1", message));
    }

    private static async Task<(Issuer Issuer, Guid Document, string Key)> NewDocumentAsync(NpgsqlConnection owner)
    {
        var issuer = await Seed.IssuerAsync(owner);
        var series = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId);
        var document = await ConstraintTests.InsertDocumentAsync(owner, issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, series, "65", 1);

        return (issuer, document, KeyFor(document));
    }

    private static Task<int> InsertDocumentAtAsync(NpgsqlConnection owner, Issuer issuer, string state) =>
        Sql.ExecuteAsync(owner,
            $"""
            insert into fiscal_document (id, tenant_id, establishment_id, environment_id, model, external_id, state, request_hash)
            values ($1, $2, $3, $4, '65', $5, '{state}', $6)
            """,
            Guid.NewGuid(), issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, $"ext-{Guid.NewGuid():N}", Hash);

    private static Task<int> SetStateAsync(NpgsqlConnection owner, Guid document, string state) =>
        Sql.ExecuteAsync(owner, $"update fiscal_document set state = '{state}' where id = $1", document);

    private static async Task WalkAsync(NpgsqlConnection owner, Guid document, string until, bool storeSentXml = true)
    {
        foreach (var state in Path)
        {
            if (state == "SIGNED")
            {
                await Sql.ExecuteAsync(owner,
                    "update fiscal_document set access_key = $2, signed_xml_sha256 = $3, dig_val = $4, signed_at = now(), state = 'SIGNED' where id = $1",
                    document, KeyFor(document), Hash, DigVal);
            }
            else
            {
                if (state == "SENDING" && storeSentXml)
                {
                    await Sql.ExecuteAsync(owner, "update fiscal_document set sent_xml_object_key = 'documents/s', sent_xml_object_version = 'v1' where id = $1 and sent_xml_object_key is null", document);
                }

                await SetStateAsync(owner, document, state);
            }

            if (state == until)
            {
                return;
            }
        }
    }

    /// <summary>A synthetic access key in the TChNFe shape (digits only), derived from the document id.</summary>
    private static string KeyFor(Guid document)
    {
        var digits = string.Concat(document.ToByteArray().Select(b => (b % 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return ("35" + digits + digits + digits)[..44];
    }

    private static async Task<Guid> ExchangeAsync(NpgsqlConnection owner, Guid tenant, Guid document, string operation, int? cStat, DateTimeOffset at, string outcome = "ANSWERED")
    {
        var id = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            """
            insert into sefaz_exchange (id, tenant_id, document_id, operation, outcome, c_stat, request_sha256, response_sha256, started_at, finished_at)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $9)
            """,
            id, tenant, document, operation, outcome, (object?)cStat ?? DBNull.Value, Hash, outcome == "ANSWERED" ? Hash : DBNull.Value, at);
        return id;
    }

    private static Task<int> ProtocolAsync(NpgsqlConnection owner, Guid tenant, Guid document, Guid exchange, int cStat, string key, string digVal) =>
        Sql.ExecuteAsync(owner,
            """
            insert into fiscal_protocol (id, tenant_id, document_id, exchange_id, kind, c_stat, x_motivo, n_prot, ch_nfe, dig_val, received_at)
            values ($1, $2, $3, $4, 'PROTOCOL', $5, 'Autorizado o uso da NF-e', '135260000000001', $6, $7, now())
            """,
            Guid.NewGuid(), tenant, document, exchange, cStat, key, digVal);
}
