using System.Text;
using FullWorth.Backend.Modules.Parity;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// MT940 and CAMT are the two formats every European bank exports even when it offers no API, which is
/// the only way to keep an account current when FullWorth cannot connect to the bank at all — Ikano over
/// FinTS, or any institution a private Enable Banking application is not enabled for.
///
/// The reason to read them rather than ask for a CSV is the closing balance: a CSV export is a list of
/// bookings, a statement states what the account was worth and on which day.
/// </summary>
public sealed class BankStatementFileTests
{
    private const string Mt940 = """
        :20:STARTUMS
        :25:DE02120300000000202051/EUR
        :28C:00012/001
        :60F:C260901EUR1000,00
        :61:2609020902D42,19NMSCNONREF//BREF-1
        :86:?00KARTENZAHLUNG?20Einkauf Wochenmarkt?32REWE Markt GmbH
        :61:2609050905C2810,44NTRFLohn//BREF-2
        :86:?00GUTSCHRIFT?20Gehalt September?32Arbeitgeber AG
        :62F:C260905EUR3768,25
        -
        """;

    private const string Camt = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
          <BkToCstmrStmt>
            <Stmt>
              <Id>STMT-1</Id>
              <Acct><Id><IBAN>DE02120300000000202051</IBAN></Id><Ccy>EUR</Ccy></Acct>
              <Bal>
                <Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp>
                <Amt Ccy="EUR">1000.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-09-01</Dt></Dt>
              </Bal>
              <Bal>
                <Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp>
                <Amt Ccy="EUR">3768.25</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-09-05</Dt></Dt>
              </Bal>
              <Ntry>
                <NtryRef>BREF-1</NtryRef>
                <Amt Ccy="EUR">42.19</Amt><CdtDbtInd>DBIT</CdtDbtInd>
                <BookgDt><Dt>2026-09-02</Dt></BookgDt><ValDt><Dt>2026-09-02</Dt></ValDt>
                <NtryDtls><TxDtls>
                  <RltdPties><Cdtr><Nm>REWE Markt GmbH</Nm></Cdtr></RltdPties>
                  <RmtInf><Ustrd>Einkauf Wochenmarkt</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
              <Ntry>
                <NtryRef>BREF-2</NtryRef>
                <Amt Ccy="EUR">2810.44</Amt><CdtDbtInd>CRDT</CdtDbtInd>
                <BookgDt><Dt>2026-09-05</Dt></BookgDt>
                <NtryDtls><TxDtls>
                  <RltdPties><Dbtr><Nm>Arbeitgeber AG</Nm></Dbtr></RltdPties>
                  <RmtInf><Ustrd>Gehalt September</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    [Fact]
    public void Mt940_reads_the_bookings_with_their_signs()
    {
        var statement = Read(Mt940);

        Assert.Equal("mt940", statement.AdapterKey);
        Assert.Equal(2, statement.Entries.Count);
        Assert.Equal(-42.19m, statement.Entries[0].Amount);
        Assert.Equal(2810.44m, statement.Entries[1].Amount);
        Assert.All(statement.Entries, entry => Assert.Equal("EUR", entry.Currency));
        Assert.Equal(new DateOnly(2026, 9, 2), statement.Entries[0].BookingDate);
    }

    [Fact]
    public void Mt940_reads_the_structured_information_field()
    {
        var statement = Read(Mt940);

        Assert.Equal("REWE Markt GmbH", statement.Entries[0].Counterparty);
        Assert.Contains("Einkauf Wochenmarkt", statement.Entries[0].Description);
        Assert.Equal("Arbeitgeber AG", statement.Entries[1].Counterparty);
    }

    // The closing balance is the whole point of preferring a statement over a CSV: it anchors the
    // account's value to a date instead of leaving a pile of bookings with no total.
    [Fact]
    public void Mt940_reads_the_closing_balance_with_its_date()
    {
        var statement = Read(Mt940);

        Assert.NotNull(statement.ClosingBalance);
        Assert.Equal(3768.25m, statement.ClosingBalance.Amount);
        Assert.Equal("EUR", statement.ClosingBalance.Currency);
        Assert.Equal(new DateOnly(2026, 9, 5), statement.ClosingBalance.AsOf);
        Assert.Equal("DE02120300000000202051/EUR", statement.AccountIdentifier);
    }

    // An overdrawn account closes on a "D" balance. Reading that as a positive number would turn a debt
    // into wealth.
    [Fact]
    public void Mt940_reads_an_overdrawn_closing_balance_as_negative()
    {
        var statement = Read(Mt940.Replace(":62F:C260905EUR3768,25", ":62F:D260905EUR231,75"));

        Assert.Equal(-231.75m, statement.ClosingBalance!.Amount);
    }

    // A reversal flips the sign of what it reverses: RC undoes a credit, so it is money leaving.
    [Fact]
    public void Mt940_reads_a_reversal_against_its_own_direction()
    {
        var statement = Read(Mt940.Replace(":61:2609050905C2810,44NTRFLohn//BREF-2", ":61:2609050905RC2810,44NTRFLohn//BREF-2"));

        Assert.Equal(-2810.44m, statement.Entries[1].Amount);
    }

    // The entry date has no year. On a January value date a December entry is last year's - dating it in
    // the current one would move the booking eleven months into the future.
    [Fact]
    public void Mt940_dates_a_december_entry_on_a_january_value_date_in_the_previous_year()
    {
        var statement = Read(Mt940
            .Replace(":60F:C260901EUR1000,00", ":60F:C260101EUR1000,00")
            .Replace(":61:2609020902D42,19NMSCNONREF//BREF-1", ":61:2601051231D42,19NMSCNONREF//BREF-1"));

        Assert.Equal(new DateOnly(2025, 12, 31), statement.Entries[0].BookingDate);
        Assert.Equal(new DateOnly(2026, 1, 5), statement.Entries[0].ValueDate);
    }

    // NONREF is the bank saying it has no reference. Using it as an identity would make every row that
    // carries it look like the same booking.
    [Fact]
    public void Mt940_never_treats_NONREF_as_an_identity()
    {
        var statement = Read(Mt940.Replace("NMSCNONREF//BREF-1", "NMSCNONREF"));

        Assert.Null(statement.Entries[0].ExternalKey);
        Assert.Equal("BREF-2", statement.Entries[1].ExternalKey);
    }

    [Fact]
    public void Camt_reads_the_bookings_the_parties_and_the_closing_balance()
    {
        var statement = Read(Camt);

        Assert.Equal("camt", statement.AdapterKey);
        Assert.Equal("DE02120300000000202051", statement.AccountIdentifier);
        Assert.Equal(2, statement.Entries.Count);
        Assert.Equal(-42.19m, statement.Entries[0].Amount);
        Assert.Equal("REWE Markt GmbH", statement.Entries[0].Counterparty);
        Assert.Equal("Einkauf Wochenmarkt", statement.Entries[0].Description);
        Assert.Equal(2810.44m, statement.Entries[1].Amount);
        Assert.Equal("Arbeitgeber AG", statement.Entries[1].Counterparty);
        Assert.Equal(3768.25m, statement.ClosingBalance!.Amount);
        Assert.Equal(new DateOnly(2026, 9, 5), statement.ClosingBalance.AsOf);
    }

    // CLBD is the booked close. CLAV includes what has not settled and OPBD is the opening figure -
    // anchoring an account with either would state a balance the bank never confirmed.
    [Fact]
    public void Camt_uses_only_the_booked_closing_balance()
    {
        var statement = Read(Camt.Replace("<Cd>CLBD</Cd>", "<Cd>CLAV</Cd>"));

        Assert.Null(statement.ClosingBalance);
        Assert.Equal(2, statement.Entries.Count);
    }

    // Real files arrive with a namespace prefix as often as with a default namespace.
    [Fact]
    public void Camt_reads_a_prefixed_namespace()
    {
        var prefixed = Camt
            .Replace("<Document xmlns=", "<ns:Document xmlns:ns=")
            .Replace("</Document>", "</ns:Document>");
        foreach (var tag in new[]
                 {
                     "BkToCstmrStmt", "Stmt", "Id", "Acct", "IBAN", "Ccy", "Bal", "Tp", "CdOrPrtry", "Cd",
                     "Amt", "CdtDbtInd", "Dt", "Ntry", "NtryRef", "BookgDt", "ValDt", "NtryDtls", "TxDtls",
                     "RltdPties", "Cdtr", "Dbtr", "Nm", "RmtInf", "Ustrd"
                 })
            prefixed = prefixed.Replace($"<{tag}>", $"<ns:{tag}>").Replace($"</{tag}>", $"</ns:{tag}>")
                .Replace($"<{tag} ", $"<ns:{tag} ");

        var statement = Read(prefixed);

        Assert.Equal(2, statement.Entries.Count);
        Assert.Equal(3768.25m, statement.ClosingBalance!.Amount);
    }

    [Fact]
    public void A_file_that_is_neither_format_is_refused_with_a_usable_message()
    {
        var error = Assert.Throws<InvalidDataException>(() => Read("Datum;Betrag\n01.09.2026;-12,34\n"));

        Assert.Contains("CSV", error.Message);
    }

    // German statements are latin-1 in practice; a mangled umlaut in a payee name is data loss.
    [Fact]
    public void A_latin1_encoded_mt940_keeps_its_umlauts()
    {
        var statement = BankStatementFile.Read(
            Encoding.Latin1.GetBytes(Mt940.Replace("REWE Markt GmbH", "Bäckerei Müller")));

        Assert.Equal("Bäckerei Müller", statement.Entries[0].Counterparty);
    }

    private static BankStatement Read(string text) => BankStatementFile.Read(Encoding.UTF8.GetBytes(text));
}
