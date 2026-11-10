// KSP load-order metadata: BDNukes loads after BDArmory and refuses to load without it.
[assembly: KSPAssembly("BDNukes", 1, 3)]
[assembly: KSPAssemblyDependency("BDArmory", 1, 0)]

// The optional BurstPQS adapter (src-burstpqs) reads the crater mod's internals.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BDNukes.BurstPQS")]
