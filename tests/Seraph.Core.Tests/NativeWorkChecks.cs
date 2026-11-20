using Seraph.Core.Framework;

internal static class NativeWorkChecks
{
    internal static void Receipts()
    {
        var ledger = new NativeWorkReceipts();
        void Near(double actual, double expected)
        { if (Math.Abs(actual - expected) > 1e-9) throw new Exception($"Native work: {actual} != {expected}"); }
        Near(ledger.Accept(1, 0, .3, false), .3);
        Near(ledger.Accept(1, .1, .3, false), 0);
        Near(ledger.Accept(2, .1, .5, false), .15);
        Near(ledger.Accept(3, .1, .5, false), 0);
        Near(ledger.Accept(4, .2, double.NaN, false), 0);
        Near(ledger.Accept(4, .2, .1, false), .1);
        Near(ledger.Accept(5, 100, .5, false), .5);
        Near(ledger.Accept(6, 100, .1, false), 0);
        Near(ledger.Accept(7, 100, .25, true), 1);
        Near(ledger.Accept(8, 100.24, .25, true), 0);
        Near(ledger.Accept(9, 100.25, .25, true), 1);
        Near(ledger.Accept(10, 99, .1, false), 0);
        Near(new NativeWorkReceipts().Accept(1, 0, .3, false), .3);
        var continuous = new NativeWorkReceipts();
        var total = 0d;
        for (var i = 1; i <= 100; i++) total += continuous.Accept(i, i * .1, .1, false);
        Near(total, 10);
    }
}
