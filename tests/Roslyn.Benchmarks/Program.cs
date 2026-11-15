using BenchmarkDotNet.Running;
using Roslyn.Benchmarks;

//BenchmarkRunner.Run<CreateWorkspaceBenchmarks>();
BenchmarkRunner.Run<ParseSolutionBenchmarks>();
