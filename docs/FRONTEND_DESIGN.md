# Frontend Design Notes

The active desktop UI is the WPF/XAML designer shell:

```text
StarBridge.Desktop/MainWindow.xaml
StarBridge.Desktop/Themes/AppTheme.xaml
```

Open `StarBridge.sln` in Visual Studio, then edit `MainWindow.xaml` with the XAML designer.

Run it with:

```powershell
.\scripts\Start Fleet Command Designer.cmd
```

The most useful theme values are in:

```text
StarBridge.Desktop/Themes/AppTheme.xaml
```

## Older Prototype

`StarBridge.App` is the older Windows Forms prototype. Keep it only as reference while the WPF desktop app is being developed.
