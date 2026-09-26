namespace Host2VMRelay;

public sealed partial class MainForm
{
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    public void ExitForTest() { quitting = true; Close(); }

    public void CaptureTabs(string path, float layoutScale = 1F)
    {
        if (AutoScaleMode != AutoScaleMode.Dpi || Font.Unit != GraphicsUnit.Point || Font.Size < 11.5F)
            throw new InvalidOperationException("DPI-aware layout or readable base font is missing.");
        wanted = true; SetConnectionControls(true); SetConnectionControls(false);
        if (!connect.Enabled || !host.Enabled) throw new InvalidOperationException("Disconnected controls did not recover.");
        if (auth.SelectedIndex == 0 && keyControls?.Enabled == true) throw new InvalidOperationException("Password mode enabled private-key browsing.");
        wanted = false; SetConnectionControls(false);
        // Synthetic layout scaling is a regression aid, not a substitute for a physical multi-DPI test.
        if (layoutScale != 1F) Scale(new SizeF(layoutScale, layoutScale));
        UiLayout.FitToScreen(this, new Size(680, 520));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        for (int i = 0; i < tabs.TabCount; i++)
        {
            tabs.SelectedIndex = i; tabs.SelectedTab!.PerformLayout(); Refresh(); Application.DoEvents();
            using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
            bitmap.Save(i == 0 ? path : Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + i + ".png"));
        }
        using (var dialog = new ScriptDialog(GenerateScript, ""))
        {
            dialog.Show(this); dialog.VerifyForTest();
            if (layoutScale != 1F) dialog.Scale(new SizeF(layoutScale, layoutScale));
            UiLayout.FitToScreen(dialog, new Size(680, 520)); dialog.PerformLayout(); dialog.Refresh(); Application.DoEvents();
            using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
            bitmap.Save(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-script.png"));
            dialog.Close();
        }
        File.WriteAllText(Path.ChangeExtension(path, ".txt"), $"PASS UI controls, script preview and stale-copy guard; DeviceDpi={DeviceDpi}; synthetic scale={layoutScale}; font={Font.SizeInPoints}pt; size={Size}");
    }

}
