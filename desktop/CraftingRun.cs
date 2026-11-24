using System;
using System.Threading;
using System.Threading.Tasks;

internal enum GearAction { Sell, Extract }

internal interface IWorkshop
{
    Task<Workshop.Balance> ReadBalance();
    Task<Workshop.Quote> OpenQuote(int materialCost);
    Task ConfirmCraft();
    Task ArrangeNewBatch();
    Task<Workshop.Disposal> ProcessRejects(GearAction action, Action<string> journal);
    Task FinishResults(Workshop.Disposal disposal);
}

internal sealed class CraftingRun
{
    public const int BatchSize = 10;

    public int Batches { get; private set; }
    public int Spent { get; private set; }
    public int Sold { get; private set; }
    public int Extracted { get; private set; }
    public int Kept { get { return Batches * BatchSize - Sold - Extracted; } }
    public int Cores { get; private set; }
    public int UnreadCoreBatches { get; private set; }
    public bool Pending { get; private set; }
    public bool PendingCraftObserved { get; private set; }
    public int PendingMaterialCost { get; private set; }
    public int FreeSlots { get; private set; }

    public async Task Execute(IWorkshop workshop, Workshop.Balance balance, int limit, int free,
        Action<string> journal, Action<CraftingRun> progress, CancellationToken token, GearAction action = GearAction.Sell)
    {
        FreeSlots = free;
        if (balance.MaterialCost <= 0)
            throw new InvalidOperationException("The selected recipe's material cost is unavailable.");
        while (limit - Spent >= balance.MaterialCost)
        {
            token.ThrowIfCancellationRequested();
            if (FreeSlots < BatchSize || balance.Materials < balance.MaterialCost)
                throw new InvalidOperationException("A batch needs enough materials and at least 10 free equipment slots.");

            var quote = await workshop.OpenQuote(balance.MaterialCost);
            if (quote.Materials != balance.MaterialCost || quote.Materials > limit - Spent ||
                quote.Gold <= 0 || quote.Gold > balance.Gold)
                throw new InvalidOperationException("The batch cost changed, exceeds the budget, or there is insufficient gold.");
            journal(string.Format("Craft click pending. Ten pieces cost {0} materials and {1} gold.", quote.Materials, quote.Gold));
            Pending = true;
            PendingMaterialCost = quote.Materials;
            await workshop.ConfirmCraft();
            await workshop.ArrangeNewBatch();
            PendingCraftObserved = true;
            journal("Ten equipment results verified. Batch sorting and balance reconciliation pending.");
            var disposal = await workshop.ProcessRejects(action, journal);
            await workshop.FinishResults(disposal);
            var after = await workshop.ReadBalance();
            if (after.MaterialCost != balance.MaterialCost || after.Materials != balance.Materials - quote.Materials)
                throw new InvalidOperationException("The material balance differs from the verified batch. Check the latest results before restarting.");
            int batchKept = BatchSize - disposal.Count;
            journal(string.Format("Batch {0} verified. Spent {1} materials and {2} gold. {3}. Kept {4}. Materials remaining {5}.",
                Batches + 1, quote.Materials, quote.Gold, disposal.Describe(action), batchKept, after.Materials));
            Pending = false;
            PendingCraftObserved = false;
            Batches++;
            Spent += quote.Materials;
            if (action == GearAction.Sell)
                Sold += disposal.Count;
            else
            {
                Extracted += disposal.Count;
                Cores += disposal.Cores.GetValueOrDefault();
                if (!disposal.Cores.HasValue)
                    UnreadCoreBatches++;
            }
            FreeSlots -= batchKept;
            balance = after;
            progress(this);
        }
    }
}
