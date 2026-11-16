using Ktc.Application.Logic;

namespace Ktc.Tests;

public class EjCounterTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    // The report from the reference image, as one EJ row with line breaks and KTC's EJ markup.
    private const string Block =
        "<EJData>_[020t 10:21:43\nMACHINE NO =ADB07076\nDATE-TIME =11/10/26 10:21:43\n" +
        "     :      TYPE1-1000 TYPE2-500\n CASSETTE      00007      01048\n+REJECTED      00002      00003\n+DISPENSED     02491      01449\n-DEPOSITED     00000      00000\n=TOTAL         02500      02500\n" +
        "     :      TYPE3-50   TYPE4-100\n CASSETTE      00000      01116\n+REJECTED      00000      00016\n+DISPENSED     00000      01868\n-DEPOSITED     00000      00000\n=TOTAL         00000      03000\n" +
        " LAST CLEARED  11/06/26 11:42</EJData>";

    [Fact]
    public void Reads_the_reference_report_from_one_ej_row()
    {
        var r = Assert.Single(EjCounterRules.Parse(new[] { ("1", Now.AddDays(-6), Block) }, Now));
        Assert.Equal("ADB07076", r.MachineNo);
        Assert.Equal(new DateTime(2026, 11, 10, 10, 21, 43), r.ReportTime);
        Assert.Equal(new DateTime(2026, 11, 6, 11, 42, 0), r.LastCleared);
        Assert.Equal(new[] { "TYPE1", "TYPE2", "TYPE3", "TYPE4" }, r.Types.Select(t => t.Type));
        Assert.Equal(new decimal?[] { 1000, 500, 50, 100 }, r.Types.Select(t => t.Denomination));
        var t1 = r.Types[0];
        Assert.Equal((7L, 2L, 2491L, 0L, 2500L), (t1.Cassette!.Value, t1.Rejected!.Value, t1.Dispensed!.Value, t1.Deposited!.Value, t1.Total!.Value));
        Assert.Equal((1116L, 16L, 1868L, 3000L), (r.Types[3].Cassette!.Value, r.Types[3].Rejected!.Value, r.Types[3].Dispensed!.Value, r.Types[3].Total!.Value));
        Assert.All(r.Types, t => Assert.True(t.Balanced));
        Assert.Equal(2491 + 1449 + 1868, r.NotesDispensed);
        Assert.Equal(7 * 1000 + 1048 * 500 + 1116 * 100, r.AmountInCassettes);
    }

    [Fact]
    public void Reads_reports_split_over_rows_newest_first_and_ignores_other_journal()
    {
        var rows = new List<(string, DateTime, string)> { ("1", Now.AddDays(-3), "TRANSACTION START"), ("2", Now.AddDays(-3), "NOTES DISPENSED: AED 500.00") };
        var lines = Block.Replace("<EJData>", "").Replace("</EJData>", "").Split('\n');
        for (int i = 0; i < lines.Length; i++) rows.Add(((10 + i).ToString(), Now.AddDays(-6), lines[i]));
        // A second, later report with two types only, dates day-first in the future as MM/dd (13 cannot be a month).
        var later = "MACHINE NO =ADB07076\nDATE-TIME =13/11/26 08:00:05\n TYPE1-1000 TYPE2-500\n CASSETTE 01000 00500\n+REJECTED 00000 00000\n+DISPENSED 00000 00000\n-DEPOSITED 00000 00000\n=TOTAL 01000 00500\n LAST CLEARED 13/11/26 07:55";
        rows.Add(("50", Now.AddDays(-3), later));
        var reps = EjCounterRules.Parse(rows, Now);
        Assert.Equal(2, reps.Count);
        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 5), reps[0].ReportTime);
        Assert.Equal(2, reps[0].Types.Count);
        Assert.Equal(4, reps[1].Types.Count);
        Assert.Equal(3000, reps[1].Types[3].Total);
    }

    [Fact]
    public void Flags_a_type_that_does_not_add_up_and_keeps_extra_rows()
    {
        var text = "MACHINE NO =X1\nDATE-TIME =11/10/26 10:00:00\n TYPE1-100\n CASSETTE 00010\n+REJECTED 00001\n+DISPENSED 00005\n+RETRACTED 00002\n=TOTAL 00020\n LAST CLEARED 11/01/26 09:00";
        var t = Assert.Single(Assert.Single(EjCounterRules.Parse(new[] { ("1", Now, text) }, Now)).Types);
        Assert.False(t.Balanced);
        Assert.Equal(2, t.Other["RETRACTED"]);
        Assert.Null(t.Deposited);
    }
}
