using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class CraftingForm : Form
{
    private readonly NumericUpDown budget = new NumericUpDown { Minimum = 1, Maximum = 1000000, Increment = 100, Value = 500, ThousandsSeparator = true };
    private readonly NumericUpDown slots = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = 0, ThousandsSeparator = true };
    private readonly Button inspect = new Button { Text = "Preview next step", AutoSize = true };
    private readonly ComboBox gearAction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly NumericUpDown clickDelay = DelayField(0.20m);
    private readonly NumericUpDown screenshotDelay = DelayField(0.30m);
    private readonly Button start = new Button { Text = "Start crafting", AutoSize = true };
    private readonly Button stop = new Button { Text = "Stop (Esc)", AutoSize = true, Enabled = false };
    private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Label totals = new Label { AutoSize = true, MaximumSize = new Size(520, 0) };
    private CancellationTokenSource active;
    private readonly System.Windows.Forms.Timer escape = new System.Windows.Forms.Timer { Interval = 35 };
    private static readonly string History = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CraftingHistory.tsv");

    private static NumericUpDown DelayField(decimal seconds)
    {
        return new NumericUpDown { Minimum = 0, Maximum = 5, DecimalPlaces = 2, Increment = 0.05m, Value = seconds };
    }

    public CraftingForm()
    {
        Text = "Epic Seven Crafting";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        var panel = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, Padding = new Padding(20)
        };
        var title = new Label { Text = "Steel Workshop", AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
        panel.Controls.Add(title);
        panel.Controls.Add(new Label {
            Text = "Choose your hunt and equipment in the Steel Workshop (Steam), then select Craft Count 10.",
            AutoSize = true, MaximumSize = new Size(510, 0)
        });

        var fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 14, 0, 10) };
        fields.Controls.Add(new Label { Text = "Material budget", AutoSize = true }, 0, 0);
        fields.Controls.Add(budget, 1, 0);
        fields.Controls.Add(new Label { Text = "Free equipment slots", AutoSize = true }, 0, 1);
        fields.Controls.Add(slots, 1, 1);
        gearAction.Items.AddRange(new object[] { GearAction.Sell, GearAction.Extract });
        gearAction.SelectedItem = GearAction.Sell;
        fields.Controls.Add(new Label { Text = "Selected gear", AutoSize = true }, 0, 2);
        fields.Controls.Add(gearAction, 1, 2);
        fields.Controls.Add(new Label { Text = "Click delay (s)", AutoSize = true }, 0, 3);
        fields.Controls.Add(clickDelay, 1, 3);
        fields.Controls.Add(new Label { Text = "Screenshot delay (s)", AutoSize = true }, 0, 4);
        fields.Controls.Add(screenshotDelay, 1, 4);
        panel.Controls.Add(fields);

        panel.Controls.Add(new Label {
            Text = "Each batch crafts 10 pieces and uses your existing Simple Selection filter.",
            AutoSize = true, MaximumSize = new Size(510, 0)
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 11, 0, 10) };
        buttons.Controls.Add(inspect);
        buttons.Controls.Add(start);
        buttons.Controls.Add(stop);
        panel.Controls.Add(buttons);
        status.Text = "Start crafting uses the current recipe and the selected gear action.";
        panel.Controls.Add(status);
        panel.Controls.Add(totals);
        Controls.Add(panel);

        inspect.Click += async (sender, args) => await Run(false);
        start.Click += async (sender, args) => await Run(true);
        stop.Click += (sender, args) => CancelRun();
        escape.Tick += (sender, args) => { if (GameWindow.EscapeDown) CancelRun(); };
        FormClosing += (sender, args) => {
            if (active != null) { args.Cancel = true; CancelRun(); }
        };
    }

    private void CancelRun()
    {
        if (active != null)
            active.Cancel();
    }

    private void ShowStatus(string value) { status.Text = value; }

    private async Task Run(bool craft)
    {
        if (active != null)
            return;
        int limit = (int)budget.Value;
        int free = (int)slots.Value;
        var action = (GearAction)gearAction.SelectedItem;
        var mouseWait = TimeSpan.FromSeconds((double)clickDelay.Value);
        var screenshotWait = TimeSpan.FromSeconds((double)screenshotDelay.Value);
        if (craft && free < 10)
        {
            ShowStatus("Enter at least 10 free slots after checking your equipment inventory.");
            return;
        }

        active = new CancellationTokenSource();
        var token = active.Token;
        var run = new CraftingRun();
        Workshop workshop = null;
        string runId = Guid.NewGuid().ToString("N").Substring(0, 12);
        bool logged = false;
        Action<string> log = entry => File.AppendAllText(History,
            DateTimeOffset.Now.ToString("o") + "\t" + runId + "\t" + entry + Environment.NewLine, Encoding.UTF8);

        start.Enabled = inspect.Enabled = budget.Enabled = slots.Enabled = gearAction.Enabled = false;
        clickDelay.Enabled = screenshotDelay.Enabled = false;
        stop.Enabled = true;
        totals.Text = "";
        try
        {
            var game = GameWindow.Find();
            ShowStatus("Bringing Epic Seven to the front.");
            escape.Start();
            await game.WaitForForeground(() => ShowStatus("Waiting for Epic Seven. Click the game window or press Esc to cancel."), token);
            ShowStatus("Reading the Steel Workshop.");
            var ocr = new WindowsOcr();
            workshop = new Workshop(game, ocr, token, mouseWait, screenshotWait, foreground =>
            {
                if (foreground == null)
                {
                    if (logged)
                        log("Epic Seven regained focus. Rechecking the game screen.");
                    ShowStatus("Epic Seven is back in front. Rechecking the screen.");
                }
                else
                {
                    if (logged)
                        log("Paused for focus. Foreground app: " + foreground + ".");
                    ShowStatus("Paused while " + foreground + " is in front. Return to Epic Seven to continue, or press Esc to stop.");
                }
            });
            if (!craft)
            {
                var preview = await workshop.PreviewOpenSelection(action);
                if (preview != null)
                {
                    ShowStatus(action == GearAction.Sell
                        ? string.Format("Sell preview. {0} selected.", preview.Count)
                        : string.Format("Extract preview. {0} selected. Cores: {1}.",
                            preview.Count, preview.Cores.HasValue ? preview.Cores.Value.ToString("N0") : "could not read total"));
                    return;
                }
            }
            var balance = await workshop.ReadBalance();
            if (!craft)
            {
                var quote = await workshop.OpenQuote(balance.MaterialCost);
                await workshop.CloseQuote();
                var afterPreview = await workshop.ReadBalance();
                if (afterPreview.Materials != balance.Materials || afterPreview.MaterialCost != balance.MaterialCost)
                    throw new InvalidOperationException("Workshop materials changed during the preview. Check the game screen.");
                ShowStatus(string.Format("Batch of 10 verified. Cost: {0:N0} materials and {1:N0} gold. Balance: {2:N0} materials and {3:N0} gold.",
                    quote.Materials, quote.Gold, balance.Materials, balance.Gold));
                return;
            }

            log(string.Format("Start. Material balance {0}, gold {1}, budget {2}, free slots {3}, selected gear action {4}.",
                balance.Materials, balance.Gold, limit, free, action));
            logged = true;
            ShowStatus("Crafting. Esc stops the run.");
            await run.Execute(workshop, balance, limit, free, log, progress =>
            {
                slots.Value = progress.FreeSlots;
                var spending = string.Format("{0:N0} materials spent in {1:N0} batches. ", progress.Spent, progress.Batches);
                totals.Text = spending + (action == GearAction.Sell
                    ? string.Format("{0:N0} sold and {1:N0} kept.", progress.Sold, progress.Kept)
                    : string.Format("{0:N0} extracted and {1:N0} kept.\nCores shown in previews: {2:N0}{3}.",
                        progress.Extracted, progress.Kept, progress.Cores,
                        progress.UnreadCoreBatches > 0 ? " (some batches unreadable)" : ""));
            }, token, action);
            ShowStatus(limit == run.Spent ? "Material budget reached." : "Remaining budget is smaller than one batch.");
            log("Material budget finished.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Stopped." + (run.Pending ? " Check the latest batch in game before restarting." : ""));
        }
        catch (Exception error)
        {
            ShowStatus(error.Message + (run.Pending ? " The latest batch requires reconciliation." : ""));
        }
        finally
        {
            if (run.PendingCraftObserved)
                status.Text += string.Format(" Ten new pieces were verified. This batch's {0:N0} materials are separate from the reconciled totals below.", run.PendingMaterialCost);
            if (logged)
            {
                try { log(string.Format("End. {0} reconciled batches, {1} materials reconciled, pending {2}, pending craft observed {3}. {4}", run.Batches, run.Spent, run.Pending, run.PendingCraftObserved, status.Text)); }
                catch (IOException) { status.Text += " Could not write the final history entry."; }
                catch (UnauthorizedAccessException) { status.Text += " Could not write the final history entry."; }
            }
            escape.Stop();
            if (workshop != null)
                workshop.Dispose();
            if (run.Pending)
                slots.Value = 0;
            active.Dispose();
            active = null;
            start.Enabled = inspect.Enabled = budget.Enabled = slots.Enabled = gearAction.Enabled = true;
            clickDelay.Enabled = screenshotDelay.Enabled = true;
            stop.Enabled = false;
        }
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
            return SelfTest().GetAwaiter().GetResult();

        GameWindow.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool first;
        using (var instance = new Mutex(true, @"Local\E7Crafting.Desktop", out first))
        {
            if (!first)
            {
                MessageBox.Show("Epic Seven Crafting is already open.", "Epic Seven Crafting");
                return 0;
            }
            Application.Run(new CraftingForm());
        }
        return 0;
    }

    private static async Task<int> SelfTest()
    {
        if (Workshop.ReadNumber(" 210 , 000 ") != 210000 ||
            Workshop.ReadNumber("2 10,000") != -1 || Workshop.ReadNumber("21,0000") != -1)
            return 1;
        try
        {
            using (var image = new Bitmap(260, 54))
            using (var g = Graphics.FromImage(image))
            using (var font = new Font("Segoe UI", 21, FontStyle.Bold))
            {
                g.Clear(Color.FromArgb(25, 40, 60));
                g.DrawString("210,000", font, Brushes.White, 12, 7);
                var ocr = new WindowsOcr();
                var text = await ocr.Read(image, new Rectangle(Point.Empty, image.Size), CancellationToken.None);
                Console.Out.WriteLine("Text recognition (" + ocr.LanguageTag + "): " + text);
                if (Workshop.ReadNumber(text) != 210000)
                    return 2;
                g.Clear(Color.FromArgb(25, 40, 60));
                g.DrawString("Simple Selection", font, Brushes.White, 12, 7);
                text = await ocr.Read(image, new Rectangle(Point.Empty, image.Size), CancellationToken.None);
                Console.Out.WriteLine("Button label: " + text);
                if (text.Trim() != "Simple Selection")
                    return 2;
            }
            await RunSelfTest.Check();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 3;
        }
    }
}
