using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal sealed class Workshop : IWorkshop, IDisposable
{
    private readonly GameWindow game;
    private readonly WindowsOcr ocr;
    private readonly CancellationToken token;
    private readonly TimeSpan clickDelay;
    private readonly TimeSpan screenshotDelay;
    private readonly Action<string> focusStatus;
    private Rectangle observedBounds;
    private Bitmap verifiedFrame;
    private ScreenArea verifiedArea;
    private Func<Bitmap, Task<bool>> verifyAgain;
    private static readonly Regex NumberPattern = new Regex(@"^(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)$");

    public Workshop(GameWindow game, WindowsOcr ocr, CancellationToken token, TimeSpan clickDelay,
        TimeSpan screenshotDelay, Action<string> focusStatus)
    {
        this.game = game;
        this.ocr = ocr;
        this.token = token;
        this.clickDelay = clickDelay;
        this.screenshotDelay = screenshotDelay;
        this.focusStatus = focusStatus;
    }

    public void Dispose()
    {
        if (verifiedFrame != null)
            verifiedFrame.Dispose();
    }

    private void Remember(Bitmap image, ScreenArea area, Func<Bitmap, Task<bool>> verifier)
    {
        var frame = (Bitmap)image.Clone();
        if (verifiedFrame != null)
            verifiedFrame.Dispose();
        verifiedFrame = frame;
        verifiedArea = area;
        verifyAgain = verifier;
    }

    private async Task ResumeFocus(FocusLostException lost)
    {
        focusStatus(lost.ForegroundApp);
        await game.WaitForFocusReturn(() => {}, token);
        focusStatus(null);
    }

    private async Task<Bitmap> CaptureWhenFocused(Action<TimeSpan> paused)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            FocusLostException interruption;
            try
            {
                return game.Capture(out observedBounds);
            }
            catch (FocusLostException lost)
            {
                interruption = lost;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await ResumeFocus(interruption);
            paused(timer.Elapsed);
        }
    }

    private static bool SameReading<T>(T expected, T current) where T : class
    {
        if (current == null)
            return false;
        var balance = expected as Balance;
        if (balance != null)
        {
            var next = (Balance)(object)current;
            return next.Materials == balance.Materials && next.Gold == balance.Gold &&
                next.MaterialCost == balance.MaterialCost;
        }
        var quote = expected as Quote;
        if (quote != null)
        {
            var next = (Quote)(object)current;
            return next.Materials == quote.Materials && next.Gold == quote.Gold;
        }
        var disposal = expected as Disposal;
        if (disposal != null)
        {
            var next = (Disposal)(object)current;
            return next.Count == disposal.Count && next.Cores == disposal.Cores;
        }
        return true;
    }

    public sealed class Balance { public int Materials, Gold, MaterialCost; }
    public sealed class Quote { public int Materials, Gold; }
    public sealed class Disposal
    {
        public int Count;
        public int? Cores;

        public string Describe(GearAction action)
        {
            return action == GearAction.Sell
                ? string.Format("Sold {0}", Count)
                : string.Format("Extracted {0}; preview cores {1}", Count, Cores.HasValue ? Cores.Value.ToString("N0") : "unreadable");
        }
    }

    public static int ReadNumber(string text)
    {
        // Accept the spaces Windows OCR inserts around commas. Reject extra words, symbols and partial numbers.
        text = Regex.Replace(text.Trim(), @"\s*,\s*", ",");
        int value;
        return NumberPattern.IsMatch(text) &&
            int.TryParse(text.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out value) ? value : -1;
    }

    internal static int ReadGoldBalanceText(string text)
    {
        int gold = ReadNumber(text);
        // The Workshop groups balances of 1,000 or more with commas.
        // Accept only the whole field so a readable prefix cannot stand in for the balance.
        return gold >= 1000 && text.IndexOf(',') < 0 ? -1 : gold;
    }

    private Task<string> Read(Bitmap image, int x, int y, int w, int h)
    {
        return ocr.Read(image, new Rectangle(x, y, w, h), token);
    }

    private Task<string> ReadWhite(Bitmap image, int x, int y, int w, int h)
    {
        return ocr.Read(image, new Rectangle(x, y, w, h), token, whiteNumbers: true);
    }

    private int GoldTextStart(Bitmap image)
    {
        // Locate the right edge of the gold coin before reading the amount.
        // Energy ends to its left and the skystone balance starts to its right.
        for (int x = 942; x >= 815; x--)
        {
            int orange = 0;
            for (int y = 23; y < 55; y++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.R > 150 && pixel.R - pixel.G > 26 && pixel.G - pixel.B > 26)
                    orange++;
            }
            if (orange >= 4)
                return x + 3;
        }
        return -1;
    }

    private async Task<int> ReadGoldDigits(Bitmap image, int start)
    {
        // Some OCR languages decline grouped numbers with several commas.
        // Isolate the tall digit glyphs and discard the shorter commas.
        const int top = 27;
        const int height = 26;
        const int right = 1060;
        var digits = new List<Rectangle>();
        using (var strip = new Bitmap(right - start, height))
        using (var pen = Graphics.FromImage(strip))
        {
            pen.Clear(Color.White);
            int left = -1, minY = height, maxY = -1, previous = -1;
            for (int x = 0; x <= strip.Width; x++)
            {
                int first = height, last = -1;
                if (x < strip.Width)
                {
                    for (int y = 0; y < height; y++)
                    {
                        var p = image.GetPixel(start + x, top + y);
                        int min = Math.Min(p.R, Math.Min(p.G, p.B));
                        int max = Math.Max(p.R, Math.Max(p.G, p.B));
                        if (min < 160 || max - min > 42)
                            continue;
                        strip.SetPixel(x, y, Color.Black);
                        first = Math.Min(first, y);
                        last = y;
                    }
                }
                if (first < height)
                {
                    if (left < 0) left = x;
                    minY = Math.Min(minY, first);
                    maxY = Math.Max(maxY, last);
                }
                else if (left >= 0)
                {
                    if (maxY - minY >= 13)
                    {
                        if (previous >= 0 && left - previous > 14)
                            break;
                        digits.Add(new Rectangle(left, 0, x - left, height));
                        previous = x;
                    }
                    left = -1;
                    minY = height;
                    maxY = -1;
                }
            }
            if (digits.Count < 6 || digits.Count > 10 || digits[0].Left > 10)
                return -1;
            using (var plain = new Bitmap(digits.Count * 15 + 32, height + 20))
            using (var graphics = Graphics.FromImage(plain))
            {
                graphics.Clear(Color.White);
                int position = 16;
                foreach (var digit in digits)
                {
                    graphics.DrawImage(strip, new Rectangle(position, 10, digit.Width, height), digit, GraphicsUnit.Pixel);
                    position += digit.Width + 3;
                }
                string text = (await ocr.Read(plain, new Rectangle(Point.Empty, plain.Size), token)).Trim();
                return text.Length == digits.Count ? ReadNumber(text) : -1;
            }
        }
    }

    private static bool Word(string text, string expected)
    {
        return string.Equals(text.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<T> WaitFor<T>(string name, Func<Bitmap, Task<T>> read, int settleMs = 160,
        Func<string> detail = null, ScreenArea area = ScreenArea.Result) where T : class
    {
        var settle = TimeSpan.FromMilliseconds(settleMs);
        await Task.Delay(settle > screenshotDelay ? settle : screenshotDelay, token);
        var end = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using (var image = await CaptureWhenFocused(duration => end += duration))
            {
                var result = await read(image);
                if (result != null)
                {
                    Remember(image, area, async next => SameReading(result, await read(next)));
                    return result;
                }
            }
            var remaining = end - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new InvalidOperationException("Could not verify " + name + ". " +
                    (detail == null ? "Check the game for a prompt or changed screen." : detail()));
            var retry = screenshotDelay > TimeSpan.FromMilliseconds(170)
                ? screenshotDelay : TimeSpan.FromMilliseconds(170);
            await Task.Delay(retry < remaining ? retry : remaining, token);
        }
    }

    private async Task Require(string name, Func<Bitmap, Task<bool>> read, int settleMs = 160,
        Func<string> detail = null)
    {
        await WaitFor(name, async image => await read(image) ? new object() : null, settleMs, detail);
    }

    public Task<Balance> ReadBalance()
    {
        string detail = "";
        return WaitFor("the Workshop, Craft Count 10 and its balances", async image =>
        {
            var workshop = await Read(image, 74, 15, 240, 47);
            var button = await Read(image, 1324, 754, 100, 54);
            var count = await Read(image, 1420, 561, 57, 37);
            detail = string.Format("Select a recipe and Craft Count 10. Read screen \"{0}\", button \"{1}\", and count \"{2}\".",
                workshop.Trim(), button.Trim(), count.Trim());
            if (!Word(workshop, "Steel Workshop") || !Word(button, "Craft") || !Word(count, "10"))
                return null;
            var countBackground = image.GetPixel(1468, 569);
            if (countBackground.R < 180 || countBackground.G < 180 || countBackground.B < 180)
            {
                detail = "Select 10 in Craft Count and close any open prompt.";
                return null;
            }
            string materialText = await ReadWhite(image, 1198, 687, 159, 32);
            var costs = materialText.Split('/');
            int start = GoldTextStart(image);
            string header = start > 0 ? await ReadWhite(image, start, 25, 1056 - start, 30) : "";
            int gold = ReadGoldBalanceText(header);
            if (gold < 0 && start > 0)
                gold = await ReadGoldDigits(image, start);
            if (gold < 0 && start > 0)
            {
                var alternate = await Read(image, start, 17, 1056 - start, 48);
                gold = ReadGoldBalanceText(alternate);
                header = string.Format("{0} / {1}", header.Trim(), alternate.Trim());
            }
            if (costs.Length != 2 || ReadNumber(costs[1]) <= 0 || ReadNumber(costs[0]) < 0 || gold < 0)
            {
                detail = string.Format("Read materials \"{0}\" and gold area \"{1}\".", materialText.Trim(), header.Trim());
                return null;
            }
            return new Balance { Materials = ReadNumber(costs[0]), Gold = gold, MaterialCost = ReadNumber(costs[1]) };
        }, detail: () => detail, area: ScreenArea.Workshop);
    }

    public async Task<Quote> OpenQuote(int materialCost)
    {
        await Click(1363, 780);
        string detail = "";
        return await WaitFor("the 10-piece crafting confirmation", async image =>
        {
            var title = await Read(image, 475, 175, 545, 73);
            var quantity = await Read(image, 555, 417, 146, 88);
            var gold = await ocr.Read(image, new Rectangle(753, 597, 129, 49), token, yellowNumbers: true);
            if (ReadNumber(gold) <= 0)
                gold = await Read(image, 759, 600, 121, 45);
            detail = string.Format("Read \"{0}\", quantity \"{1}\", gold cost \"{2}\".",
                title.Trim(), quantity.Trim(), gold.Trim());
            if ((!Word(title, "Craft 10 Pieces of Equipment") &&
                 !(Regex.IsMatch(title.Trim(), @"^Craft (?:IO|l0|10) Pieces of Equipment\??$", RegexOptions.IgnoreCase) &&
                   ContainsCraftTen(quantity))) ||
                ReadNumber(gold) <= 0 ||
                !await Label(image, 901, 595, 112, 51, "Craft"))
                return null;
            return new Quote { Materials = materialCost, Gold = ReadNumber(gold) };
        }, detail: () => detail, area: ScreenArea.Dialog);
    }

    private static bool ContainsCraftTen(string text)
    {
        return Regex.IsMatch(text.Trim(), @"^Craft\s+10\s+Times$", RegexOptions.IgnoreCase);
    }

    public Task ConfirmCraft()
    {
        return Click(950, 619);
    }

    public Task CloseQuote()
    {
        return Click(565, 619);
    }

    private Task<bool> Success(Bitmap image) { return Label(image, 652, 97, 190, 69, "Success"); }

    private async Task<bool> Label(Bitmap image, int x, int y, int w, int h, string word)
    {
        return Word(await Read(image, x, y, w, h), word);
    }

    private async Task<int> CountGear(Bitmap image, int expected)
    {
        if (expected == 0)
            return await Label(image, 590, 405, 320, 55, "There is no Equipment.") ? 0 : -1;
        return CountGearCards(image);
    }

    internal static int CountGearCards(Bitmap image)
    {
        const int firstColumn = 140;
        const int lastColumn = 1185;
        const int minEdgeDistance = 88;
        const int maxEdgeDistance = 104;
        var edges = new bool[lastColumn - firstColumn + 1];
        int count = 0;
        foreach (int top in new[] { 245, 380, 515 })
        {
            // The illustration can hide the rarity color in the middle of a tile.
            // Look for two sustained vertical sides above the set badge.
            for (int column = 0; column < edges.Length; column++)
            {
                int edgeSamples = 0;
                for (int y = top + 24; y < top + 66; y += 3)
                {
                    var p = image.GetPixel(firstColumn + column, y);
                    int min = Math.Min(p.R, Math.Min(p.G, p.B));
                    int max = Math.Max(p.R, Math.Max(p.G, p.B));
                    if (max >= 90 || (max >= 58 && max - min >= 32))
                        edgeSamples++;
                }
                edges[column] = edgeSamples >= 11;
            }
            for (int left = 0; left + minEdgeDistance < edges.Length; left++)
            {
                if (!edges[left])
                    continue;
                int rightStart = left + minEdgeDistance;
                int rightLength = Math.Min(maxEdgeDistance - minEdgeDistance + 1, edges.Length - rightStart);
                if (Array.IndexOf(edges, true, rightStart, rightLength) < 0)
                    continue;
                count++;
                left += maxEdgeDistance;
            }
        }
        return count;
    }

    public Task VerifyNewBatch()
    {
        string detail = "";
        return Require("ten equipment results", async image =>
        {
            var title = (await Read(image, 652, 97, 190, 69)).Trim();
            var arrange = (await Read(image, 1217, 645, 142, 47)).Trim();
            int cards = CountGearCards(image);
            if (Word(title, "Success") && Word(arrange, "Arrange") && cards == 10)
                return true;

            var quoteTitle = await Read(image, 475, 175, 545, 73);
            string reading = Word(quoteTitle, "Craft 10 Pieces of Equipment")
                ? "The craft confirmation is still open."
                : string.Format("Read title \"{0}\", Arrange area \"{1}\", and {2} gear cards.", title, arrange, cards);
            detail = string.Format("{0} Game area: {1} × {2}.", reading, observedBounds.Width, observedBounds.Height);
            return false;
        }, 1700, () => detail);
    }

    public async Task ArrangeNewBatch()
    {
        await Click(1285, 668);
        string detail = "";
        await Require("Simple Selection with no gear selected", async image =>
        {
            var selection = (await Read(image, 79, 749, 158, 47)).Trim();
            var count = (await ReadSelectedText(image)).Trim();
            detail = string.Format("Read selection button \"{0}\" and count \"{1}\". Game area: {2} × {3}.",
                selection, count, observedBounds.Width, observedBounds.Height);
            return Word(selection, "Simple Selection") && ParseSelected(count) == 0 &&
                await Success(image) && await Label(image, 1226, 645, 119, 46, "Cancel");
        }, detail: () => detail);
    }

    private async Task<bool> IsArrange(Bitmap image)
    {
        return await Success(image) && await Label(image, 79, 749, 158, 47, "Simple Selection") &&
            await Label(image, 1226, 645, 119, 46, "Cancel");
    }

    private Task<string> ReadSelectedText(Bitmap image)
    {
        return Read(image, 674, 650, 158, 47);
    }

    private async Task<int> Selected(Bitmap image)
    {
        return ParseSelected(await ReadSelectedText(image));
    }

    internal static int ParseSelected(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(10|[0-9O])\s+selected$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return -1;
        var count = match.Groups[1].Value;
        return Word(count, "O") ? 0 : ReadNumber(count);
    }

    public async Task<Disposal> ProcessRejects(GearAction action, Action<string> journal)
    {
        await Click(155, 772);
        var selected = await WaitFor("the Simple Selection count", async image =>
        {
            if (!await IsArrange(image))
                return null;
            int n = await Selected(image);
            return n < 0 ? null : new Disposal { Count = n, Cores = 0 };
        }, 650);
        if (selected.Count == 0)
            return selected;
        var disposal = await OpenDisposal(action, selected.Count);
        journal(action + " confirmation pending. " + disposal.Describe(action) + ".");
        await Click(873, 617);
        return disposal;
    }

    public async Task<Disposal> PreviewOpenSelection(GearAction action)
    {
        await Task.Delay(screenshotDelay, token);
        int count;
        using (var image = await CaptureWhenFocused(ignored => {}))
        {
            // A failed run may have already opened the sale confirmation.
            // Preview can read it directly and leave the pending choice intact.
            if (action == GearAction.Sell && await IsSaleDialog(image))
                return await ReadSale(null);
            if (!await IsArrange(image))
                return null;
            count = await Selected(image);
            if (count >= 1)
                Remember(image, ScreenArea.Result, async next => await IsArrange(next) && await Selected(next) == count);
        }
        if (count < 1)
            throw new InvalidOperationException("Apply Simple Selection in the game to preview the selected gear.");
        var disposal = await OpenDisposal(action, count);
        await Click(623, 617);
        await Require("the original selection after closing the preview", async image =>
            await IsArrange(image) && await Selected(image) == count);
        return disposal;
    }

    private async Task<Disposal> OpenDisposal(GearAction action, int count)
    {
        // Arrange and the nonzero selection identify the action row. The next
        // click only opens a dialog; the dialog verifies which action was opened.
        await Click(action == GearAction.Sell ? 472 : 721, 774);
        return action == GearAction.Sell ? await ReadSale(count) : await ReadExtraction(count);
    }

    private async Task<bool> IsSaleDialog(Bitmap image)
    {
        return await Label(image, 680, 179, 136, 65, "Sell") &&
            await Label(image, 790, 588, 166, 49, "Confirm");
    }

    internal static int ReadSaleCount(string question)
    {
        var match = Regex.Match(question, @"\bsell\s+(10|[1-9])\s+selected\b", RegexOptions.IgnoreCase);
        return match.Success ? ReadNumber(match.Groups[1].Value) : -1;
    }

    private Task<Disposal> ReadSale(int? expected)
    {
        string detail = "";
        return WaitFor("the selected count in the Sell dialog", async image =>
        {
            if (!await IsSaleDialog(image))
                return null;
            string question = await Read(image, 478, 283, 549, 60);
            int count = ReadSaleCount(question);
            detail = expected.HasValue
                ? string.Format("Expected {0} selected. Read \"{1}\".", expected.Value, question.Trim())
                : string.Format("Read \"{0}\".", question.Trim());
            if (count < 1 || (expected.HasValue && count != expected.Value))
                return null;
            return new Disposal { Count = count };
        }, detail: () => detail, area: ScreenArea.Dialog);
    }

    private Task<Disposal> ReadExtraction(int count)
    {
        return WaitFor("the Equipment Extraction confirmation", async image =>
        {
            if (!await Label(image, 533, 178, 430, 65, "Equipment Extraction") ||
                !await Label(image, 790, 588, 166, 49, "Extract"))
                return null;
            var question = await Read(image, 410, 278, 680, 69);
            if (question.IndexOf("extract the Equipment", StringComparison.OrdinalIgnoreCase) < 0 ||
                question.IndexOf("Core", StringComparison.OrdinalIgnoreCase) < 0)
                return null;
            return new Disposal { Count = count, Cores = await ReadCorePreview(image) };
        }, area: ScreenArea.Dialog);
    }

    private async Task<int?> ReadCorePreview(Bitmap image)
    {
        var found = new List<int>();
        // The centered row uses either the odd (one/three) or even (two/four) icon positions.
        for (int offset = -3; offset <= 3; offset++)
        {
            int x = 748 + offset * 63;
            int cyan = 0;
            for (int y = 400; y <= 452; y += 3)
            for (int px = x - 22; px <= x + 22; px += 3)
            {
                var p = image.GetPixel(px, y);
                if (p.B >= 140 && p.G >= 115 && p.G - p.R >= 30)
                    cyan++;
            }
            if (cyan >= 12)
                found.Add(offset);
        }
        if (found.Count == 0 || found.Count > 4)
            return null;
        int total = 0;
        for (int i = 0; i < found.Count; i++)
        {
            int expected = 2 * i - (found.Count - 1);
            if (found[i] != expected)
                return null;
            int x = 748 + expected * 63;
            int cores = ReadNumber(await ReadWhite(image, x - 25, 462, 50, 28));
            if (cores <= 0)
                cores = ReadNumber(await Read(image, x - 25, 461, 50, 30));
            if (cores <= 0)
                return null;
            total += cores;
        }
        return total;
    }

    public async Task FinishResults(Disposal disposal)
    {
        if (disposal.Count == 10)
        {
            await Require("an empty result list after processing all ten", async image =>
                await Success(image) && await CountGear(image, 0) == 0 &&
                await Label(image, 470, 742, 190, 51, "Confirm"), 550);
            await Click(558, 763);
        }
        else
        {
            await Require("the remaining equipment after sorting", async image =>
                await IsArrange(image) && await Selected(image) == 0 &&
                await CountGear(image, 10 - disposal.Count) == 10 - disposal.Count &&
                await Label(image, 865, 747, 169, 51, "Confirm"), 550);
            await Click(949, 772);
        }
    }

    private async Task Click(int x, int y)
    {
        var target = new Point(x, y);
        while (true)
        {
            FocusLostException interruption;
            try
            {
                await game.Click(target, observedBounds, clickDelay, token);
                return;
            }
            catch (FocusLostException lost)
            {
                interruption = lost;
            }
            // GameWindow only raises this before input is sent, so we can
            // recheck the same action without repeating a completed click.
            var originalBounds = observedBounds;
            await ResumeFocus(interruption);
            using (var current = await CaptureWhenFocused(ignored => {}))
            {
                if (originalBounds != observedBounds || verifiedFrame == null ||
                    !ScreenWitness.Matches(verifiedFrame, current, verifiedArea) ||
                    !await verifyAgain(current))
                    throw new InvalidOperationException("The game screen changed while paused. Review the current batch before restarting.");
            }
        }
    }
}
