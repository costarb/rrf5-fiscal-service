using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rrf5.Fiscal.Application.Documents;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>
/// Documents, items, payments, SEFAZ exchanges, protocols and the outbox (migrations 0011,
/// 0013 and 0014). State changes go through <c>fiscal_document_transition</c>, so the
/// database refuses any edge outside the closed state machine.
/// </summary>
public sealed class PostgresDocumentRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : IDocumentRepository
{
    private const string Tenant = "tenant_id = app_current_tenant()";

    private const string Columns = """
        id, tenant_id, establishment_id, environment_id, series_id, external_id, state, emission_mode, number, access_key,
        issued_at, signed_at, dig_val, total_amount, change_amount, consumer_cpf, consumer_name, tax_rule_snapshot,
        signed_xml_sha256, sent_xml_object_key, sent_xml_object_version, proc_xml_sha256, proc_xml_object_key,
        proc_xml_object_version, authorized_at, updated_at
        """;

    public async Task InsertAsync(NewDocument document, IReadOnlyList<DocumentItemRecord> items, IReadOnlyList<DocumentPaymentRecord> payments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var d = document;
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(payments);
        await using (var insert = Command(
            """
            insert into fiscal_document (id, tenant_id, establishment_id, environment_id, series_id, model, external_id, state, request_hash,
                                         emission_mode, total_amount, change_amount, consumer_cpf, consumer_name, tax_rule_snapshot)
            values ($1, $2, $3, $4, null, '65', $5, 'READY_TO_SIGN', $6, $7, $8, $9, $10, $11, $12)
            """,
            d.Id, d.TenantId, d.EstablishmentId, d.EnvironmentId, d.ExternalId, d.RequestHash, d.EmissionMode, d.Total, d.Change,
            (object?)d.ConsumerCpf ?? DBNull.Value, (object?)d.ConsumerName ?? DBNull.Value, Json(d.TaxRuleSnapshot)))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var i in items)
        {
            await using var item = Command(
                """
                insert into fiscal_document_item (id, tenant_id, document_id, item_number, product_code, description, item_class, unit, quantity,
                                                  unit_price, line_value, discount, surcharge, tax_rule_version_id, gtin, ncm, cest, origin, cfop, csosn)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20)
                """,
                Guid.NewGuid(), d.TenantId, d.Id, (short)i.Number, i.ProductCode, i.Description, i.ItemClass, i.Unit, i.Quantity,
                i.UnitPrice, i.LineValue, i.Discount, i.Surcharge, i.TaxRuleVersionId, i.Gtin, i.Ncm, (object?)i.Cest ?? DBNull.Value,
                i.Origin, i.Cfop, i.Csosn);
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var p in payments)
        {
            await using var payment = Command(
                """
                insert into fiscal_document_payment (id, tenant_id, document_id, payment_number, commercial_method, t_pag, description, amount,
                                                     card_integration, acquirer_cnpj, card_brand, authorization_code)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
                """,
                Guid.NewGuid(), d.TenantId, d.Id, (short)p.Number, p.Method, p.TPag, (object?)p.Description ?? DBNull.Value, p.Amount,
                p.CardIntegration is { } integration ? (short)integration : DBNull.Value, (object?)p.AcquirerCnpj ?? DBNull.Value,
                (object?)p.CardBrand ?? DBNull.Value, (object?)p.AuthorizationCode ?? DBNull.Value);
            await payment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<DocumentRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ReadOneAsync($"select {Columns} from fiscal_document where id = $1 and {Tenant}", id, cancellationToken);

    public Task<DocumentRecord?> LockAsync(Guid id, CancellationToken cancellationToken) =>
        ReadOneAsync($"select {Columns} from fiscal_document where id = $1 and {Tenant} for update", id, cancellationToken);

    public async Task<IReadOnlyList<DocumentItemRecord>> ListItemsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            select item_number, product_code, description, item_class, unit, quantity, unit_price, line_value, discount, surcharge,
                   tax_rule_version_id, gtin, ncm, cest, origin, cfop, csosn
            from fiscal_document_item where document_id = $1 and {Tenant} order by item_number
            """,
            documentId);
        var items = new List<DocumentItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new DocumentItemRecord(
                reader.GetInt16(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDecimal(5),
                reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetGuid(10), reader.GetString(11),
                reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetString(16)));
        }

        return items;
    }

    public async Task<IReadOnlyList<DocumentPaymentRecord>> ListPaymentsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            select payment_number, commercial_method, t_pag, description, amount, card_integration, acquirer_cnpj, card_brand, authorization_code
            from fiscal_document_payment where document_id = $1 and {Tenant} order by payment_number
            """,
            documentId);
        var payments = new List<DocumentPaymentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            payments.Add(new DocumentPaymentRecord(
                reader.GetInt16(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetInt16(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return payments;
    }

    public async Task BindNumberAsync(Guid documentId, Guid reservationId, Guid seriesId, long number, string accessKey, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        await using (var bind = Command($"update number_reservation set document_id = $1, state = 'BOUND', updated_at = now() where id = $2 and state = 'RESERVED' and {Tenant}", documentId, reservationId))
        {
            if (await bind.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The reservation is not RESERVED.");
            }
        }

        await using var update = Command(
            $"update fiscal_document set series_id = $2, number = $3, access_key = $4, issued_at = $5, version = version + 1, updated_at = now() where id = $1 and {Tenant}",
            documentId, seriesId, number, accessKey, issuedAt);
        await RequireOneAsync(update, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshIssuedAtAsync(Guid documentId, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        await using var update = Command(
            $"update fiscal_document set issued_at = $2, version = version + 1, updated_at = now() where id = $1 and signed_at is null and {Tenant}",
            documentId, issuedAt);
        await RequireOneAsync(update, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSignedAsync(Guid documentId, SignedFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var f = fields;
        await using var update = Command(
            $"""
            update fiscal_document
            set signed_at = $2, dig_val = $3, signed_xml_sha256 = $4, certificate_fingerprint = $5, schema_reference_id = $6,
                sent_xml_object_key = $7, sent_xml_object_version = $8, state = 'SIGNED', version = version + 1, updated_at = now()
            where id = $1 and {Tenant}
            """,
            documentId, f.SignedAt, f.DigVal, f.SignedXmlSha256, f.CertificateFingerprint, f.SchemaReferenceId, f.SentXmlObjectKey,
            (object?)f.SentXmlObjectVersion ?? DBNull.Value);
        await RequireOneAsync(update, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetStateAsync(Guid documentId, string state, CancellationToken cancellationToken)
    {
        await using var update = Command($"update fiscal_document set state = $2, version = version + 1, updated_at = now() where id = $1 and {Tenant}", documentId, state);
        await RequireOneAsync(update, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkAuthorizedAsync(Guid documentId, string procSha256, string procObjectKey, string? procObjectVersion, DateTimeOffset authorizedAt, CancellationToken cancellationToken)
    {
        await using var update = Command(
            $"""
            update fiscal_document
            set proc_xml_sha256 = $2, proc_xml_object_key = $3, proc_xml_object_version = $4, authorized_at = $5, state = 'AUTHORIZED',
                version = version + 1, updated_at = now()
            where id = $1 and {Tenant}
            """,
            documentId, procSha256, procObjectKey, (object?)procObjectVersion ?? DBNull.Value, authorizedAt);
        await RequireOneAsync(update, cancellationToken).ConfigureAwait(false);
    }

    public async Task InsertExchangeAsync(NewExchange exchange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var e = exchange;
        await using var insert = Command(
            """
            insert into sefaz_exchange (id, tenant_id, document_id, operation, outcome, c_stat, request_sha256, request_object_key, request_object_version,
                                        response_sha256, response_object_key, response_object_version, http_status, started_at, finished_at)
            values ($1, app_current_tenant(), $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            """,
            e.Id, e.DocumentId, e.Operation, e.Outcome, (object?)e.CStat ?? DBNull.Value, e.RequestSha256, (object?)e.RequestObjectKey ?? DBNull.Value,
            (object?)e.RequestObjectVersion ?? DBNull.Value, (object?)e.ResponseSha256 ?? DBNull.Value, (object?)e.ResponseObjectKey ?? DBNull.Value,
            (object?)e.ResponseObjectVersion ?? DBNull.Value, e.HttpStatus is { } http ? (short)http : DBNull.Value, e.StartedAt, e.FinishedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task InsertProtocolAsync(NewProtocol protocol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var p = protocol;
        await using var insert = Command(
            """
            insert into fiscal_protocol (id, tenant_id, document_id, exchange_id, kind, c_stat, x_motivo, n_prot, ch_nfe, dig_val, received_at)
            values ($1, app_current_tenant(), $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """,
            Guid.NewGuid(), p.DocumentId, p.ExchangeId, p.Kind, p.CStat, p.XMotivo, (object?)p.NProt ?? DBNull.Value, (object?)p.ChNfe ?? DBNull.Value,
            (object?)p.DigVal ?? DBNull.Value, p.ReceivedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountExchangesAsync(Guid documentId, string operation, CancellationToken cancellationToken)
    {
        await using var command = Command($"select count(*) from sefaz_exchange where document_id = $1 and operation = $2 and {Tenant}", documentId, operation);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<ProtocolRecord?> LatestProtocolAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"select kind, c_stat, x_motivo, n_prot, received_at from fiscal_protocol where document_id = $1 and {Tenant} order by received_at desc, id limit 1",
            documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProtocolRecord(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    public async Task EnqueueAsync(Guid documentId, string kind, DateTimeOffset availableAt, CancellationToken cancellationToken)
    {
        await using var insert = Command(
            "insert into document_outbox (id, tenant_id, document_id, kind, available_at) values ($1, app_current_tenant(), $2, $3, $4)",
            Guid.NewGuid(), documentId, kind, availableAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutboxMessage?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            select id, document_id, kind, attempts from document_outbox
            where published_at is null and available_at <= now() and {Tenant}
            order by available_at, created_at
            limit 1
            for update skip locked
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new OutboxMessage(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3))
            : null;
    }

    public async Task CompleteAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var update = Command($"update document_outbox set published_at = now(), attempts = attempts + 1 where id = $1 and {Tenant}", messageId);
        await RequireOneAsync(update, cancellationToken).ConfigureAwait(false);
    }

    public async Task RescheduleAsync(Guid messageId, DateTimeOffset availableAt, CancellationToken cancellationToken)
    {
        await using var update = Command($"update document_outbox set available_at = $2, attempts = attempts + 1 where id = $1 and {Tenant}", messageId, availableAt);
        await RequireOneAsync(update, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocumentRecord?> ReadOneAsync(string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(sql, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        DateTimeOffset? Time(int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
        return new DocumentRecord(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetString(5), reader.GetString(6), Text(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), Text(9), Time(10), Time(11),
            Text(12), reader.IsDBNull(13) ? null : reader.GetDecimal(13), reader.GetDecimal(14), Text(15), Text(16),
            reader.IsDBNull(17) ? null : JsonDocument.Parse(reader.GetString(17)).RootElement.Clone(),
            Text(18), Text(19), Text(20), Text(21), Text(22), Text(23), Time(24), reader.GetFieldValue<DateTimeOffset>(25));
    }

    private static async Task RequireOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The document row was not updated.");
        }
    }

    private static NpgsqlParameter Json(JsonElement value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value.GetRawText() };

    private NpgsqlCommand Command(string sql, params object?[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg as NpgsqlParameter ?? new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }

        return command;
    }
}
