using MabiTool.Observation.Worker;

if (!OperatingSystem.IsWindows()) { Environment.ExitCode = 2; return; }
try { await ObservationWorkerApplication.RunAsync(new NpcapObservationSource()); }
catch { Environment.ExitCode = 3; }
