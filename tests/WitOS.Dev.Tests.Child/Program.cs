using WitOS.Dev.Tests.Child;

// Child process of the host tests: dotnet WitOS.Dev.Tests.Child.dll <mode> [arguments].
return args.Length == 0 ? ChildModes.UNKNOWN_MODE : await ChildModes.RunAsync(args[0], args[1..]);
