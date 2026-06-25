# Search Fields Compile Fix

Fixes `SignalDefinition` compatibility for the search-field patch.

## Fixed

- Added `SignalDefinition.ReportPlanReason` as a read-only compatibility alias to `ReportCoverageReason`.
- This resolves build errors from the expanded search haystack in:
  - `IedConfigurationWizardWindow.xaml.cs`
  - `MainWindow.xaml.cs`

Canonical model property remains `ReportCoverageReason`.
