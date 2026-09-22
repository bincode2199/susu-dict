using Jint;
using Jint.Native;
using System.Runtime.CompilerServices;

if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Publish NativeAOT before running this comparison.");
using var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromMilliseconds(100)).LimitMemory(16 * 1024 * 1024));
if (engine.Evaluate("JSON.stringify({text:'中文',value:21*2})").AsString() != "{\"text\":\"中文\",\"value\":42}") throw new InvalidOperationException("Jint result mismatch.");
Console.WriteLine("Jint AOT evaluation passed.");
