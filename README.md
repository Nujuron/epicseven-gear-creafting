# Epic Seven Crafting

Download [E7Crafting-Simple.zip](https://github.com/Nujuron/epicseven-gear-creafting/raw/refs/heads/main/downloads/E7Crafting-Simple.zip), extract it into a writable folder, and open `E7Crafting-Simple.exe`. Close any older instance first. The app follows the recipe you select in the English Steam client's Steel Workshop. It uses Windows text recognition and runs without Python.

1. Open the Steel Workshop, choose a hunt and equipment slot, then select **Craft Count 10**. Use a fully visible 16:9 game window at least 1200 pixels wide. If an old confirmation dialog is open, cancel it first.
2. Enter your material budget and the number of free equipment slots. Choose **Sell** or **Extract** in **Selected gear**. **Preview next step** opens and cancels the craft quote or, if Arrange already shows selected gear, previews the chosen action.
3. Click **Start crafting**. The app brings Epic Seven forward or waits for you to click the game window. Press **Esc** to stop. The app stops at a resource limit or if it cannot verify a batch.

If another app takes focus mid-run, crafting pauses and the history records which process was in front. Return to Epic Seven to resume. The app rechecks the game before sending a pending click and stops if the checked screen changed.

**Click delay** defaults to 0.20 seconds between moving the pointer to a button and pressing it. The app gives the game at least 0.10 seconds to register the new pointer position. **Screenshot delay** defaults to 0.30 seconds before reading the screen. Both are adjustable before a run, and steps with longer animation waits keep those waits.

Each batch uses the recipe's material cost from the Workshop and checks for enough gold before crafting. The app applies the existing Simple Selection filter, checks the chosen action's confirmation, and verifies the remaining gear and material balance after the batch. Selling depends on the selected count and result, with no reward amounts to read. The log distinguishes pieces sold from pieces extracted. Core counts come from the extraction preview when readable. Inventory space is estimated from the free-slot count you enter and the number kept per batch.

`CraftingHistory.tsv` is written beside the app. It records starting balances, actions about to be taken, completed batches, and any batch left pending. If a batch is pending, reconcile it in game and enter your current inventory space and remaining budget before starting a new run.

The built-in self-test covers text recognition, budget handling, and interruption. It runs separately from the game.

To rebuild from source with the Windows SDK installed, run `./build.ps1` from PowerShell. It writes the executable to `outputs/`.
