using CpuExecutionChecks;

OriginChecks.Run(Console.WriteLine);
Console.WriteLine("RT2_PREFIX_HASH_CPU " + GpuResourceSmoke.PrefixHashCpuChecks.Run());
CpuPortabilityIntegration.Run(Console.WriteLine);
if (args.Contains("--portability", StringComparer.Ordinal)) return;
CpuExecutionIntegration.Run(Console.WriteLine);
ProductionSessionIntegration.Run(Console.WriteLine);
