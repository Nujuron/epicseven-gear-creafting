using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

internal static class RunSelfTest
{
    private sealed class Replay : IWorkshop
    {
        public int CraftClicks;
        public bool BadBalance;
        public readonly Workshop.Balance Starting = new Workshop.Balance { Materials = 1600, Gold = 2000000, MaterialCost = 400 };
        public int GoldCost = 210000;
        public int? ChangedQuote;
        public GearAction LastAction;

        public Task<Workshop.Balance> ReadBalance()
        {
            return Task.FromResult(new Workshop.Balance {
                Materials = Starting.Materials - CraftClicks * Starting.MaterialCost + (BadBalance ? 1 : 0),
                MaterialCost = Starting.MaterialCost,
                Gold = Starting.Gold - CraftClicks * GoldCost + (LastAction == GearAction.Sell ? 1739 : 0)
            });
        }

        public Task<Workshop.Quote> OpenQuote(int materialCost)
        {
            return Task.FromResult(new Workshop.Quote { Materials = ChangedQuote ?? materialCost, Gold = GoldCost });
        }
        public Task ConfirmCraft()
        {
            CraftClicks++;
            return Task.CompletedTask;
        }

        public Task ArrangeNewBatch() { return Task.CompletedTask; }
        public Task<Workshop.Disposal> ProcessRejects(GearAction action, Action<string> journal)
        {
            LastAction = action;
            return Task.FromResult(action == GearAction.Sell
                ? new Workshop.Disposal { Count = 8 }
                : new Workshop.Disposal { Count = 8, Cores = 68 });
        }
        public Task FinishResults(Workshop.Disposal disposal) { return Task.CompletedTask; }
    }

    public static async Task Check()
    {
        if (Workshop.ReadSaleCount("Do you want to sell 10 selected item(s)?") != 10 ||
            Workshop.ReadSaleCount("Do you want to sell 1 selected item(s)?") != 1 ||
            Workshop.ReadSaleCount("Do you want to sell 100 selected item(s)?") != -1)
            throw new Exception("Could not distinguish the number of pieces in the Sell dialog.");

        using (var reference = new Bitmap(GameWindow.Width, GameWindow.Height))
        using (var current = new Bitmap(GameWindow.Width, GameWindow.Height))
        using (var graphics = Graphics.FromImage(current))
        {
            graphics.FillRectangle(Brushes.White, 900, 16, 140, 40); // A change in the HUD is outside the results check.
            graphics.Flush();
            if (!ScreenWitness.Matches(reference, current, ScreenArea.Result))
                throw new Exception("A HUD change prevented resuming unchanged results.");
            graphics.FillRectangle(Brushes.Green, 398, 271, 20, 24);
            graphics.Flush();
            if (ScreenWitness.Matches(reference, current, ScreenArea.Result))
                throw new Exception("A changed selection was accepted after a focus pause.");
        }

        int reads = 0;
        int notices = 0;
        bool[] focus = { false, true, true, false, true, true, true };
        await GameWindow.WaitForStableFocus(() => true, () => focus[reads++],
            () => notices++, CancellationToken.None, 1);
        if (reads != focus.Length || notices != 1)
            throw new Exception("The startup handoff accepted a transient focus change.");
        using (var cancelled = new CancellationTokenSource())
        {
            try
            {
                await GameWindow.WaitForStableFocus(() => true, () => false,
                    cancelled.Cancel, cancelled.Token, 1);
                throw new Exception("Waiting for focus could not be cancelled.");
            }
            catch (OperationCanceledException) {}
        }

        var replay = new Replay();
        var run = new CraftingRun();
        await run.Execute(replay, replay.Starting, 1199, 12, ignored => {}, ignored => {}, CancellationToken.None);
        if (replay.CraftClicks != 2 || run.Spent != 800 || run.Sold != 16 || run.Kept != 4 ||
            run.FreeSlots != 8 || run.Pending)
            throw new Exception("Budget or inventory accounting depended on sale proceeds.");

        replay = new Replay { GoldCost = 300000 };
        replay.Starting.MaterialCost = 500;
        run = new CraftingRun();
        await run.Execute(replay, replay.Starting, 900, 20, ignored => {}, ignored => {}, CancellationToken.None);
        if (replay.CraftClicks != 1 || run.Spent != 500 || run.Kept != 2)
            throw new Exception("The run used weapon costs for a different recipe.");

        replay = new Replay();
        run = new CraftingRun();
        await run.Execute(replay, replay.Starting, 400, 20, ignored => {}, ignored => {}, CancellationToken.None, GearAction.Extract);
        if (replay.LastAction != GearAction.Extract || run.Extracted != 8 || run.Kept != 2 ||
            run.Sold != 0 || run.Cores != 68)
            throw new Exception("Extraction was accounted for as a sale.");

        replay = new Replay { ChangedQuote = 500 };
        run = new CraftingRun();
        try
        {
            await run.Execute(replay, replay.Starting, 400, 20, ignored => {}, ignored => {}, CancellationToken.None);
            throw new Exception("The run accepted a changed cost.");
        }
        catch (InvalidOperationException)
        {
            if (replay.CraftClicks != 0 || run.Pending)
                throw new Exception("A changed quote caused a craft confirmation.");
        }

        replay = new Replay { BadBalance = true };
        run = new CraftingRun();
        try
        {
            await run.Execute(replay, replay.Starting, 800, 12, ignored => {}, ignored => {}, CancellationToken.None);
            throw new Exception("The run accepted a mismatched balance.");
        }
        catch (InvalidOperationException)
        {
            if (replay.CraftClicks != 1 || run.Spent != 0 || !run.Pending)
                throw new Exception("The run lost track of an unverified batch.");
        }

        replay = new Replay();
        run = new CraftingRun();
        try
        {
            await run.Execute(replay, replay.Starting, 800, 10, ignored => {}, ignored => {}, CancellationToken.None);
            throw new Exception("The run accepted insufficient inventory.");
        }
        catch (InvalidOperationException)
        {
            if (replay.CraftClicks != 1 || run.Spent != 400 || run.Pending)
                throw new Exception("The run miscounted after reaching the inventory limit.");
        }

        replay = new Replay();
        run = new CraftingRun();
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                await run.Execute(replay, replay.Starting, 800, 20, ignored => {}, ignored => cancellation.Cancel(), cancellation.Token);
                throw new Exception("The run ignored stop.");
            }
            catch (OperationCanceledException)
            {
                if (replay.CraftClicks != 1 || run.Spent != 400 || run.Pending)
                    throw new Exception("The run did not stop cleanly after cancellation.");
            }
        }
        Console.Out.WriteLine("Run replay: budget, stock, balance reconciliation and stop paths passed.");
    }
}
