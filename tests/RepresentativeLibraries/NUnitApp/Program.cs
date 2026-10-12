// The NUnit application's entry (plan step N2.1): NUnitLite runs the assembly's tests and returns the failures. It
// writes no result file, since a test's working directory may be read-only.
using NUnitLite;

return new AutoRun(typeof(Program).Assembly).Execute(["--noresult", .. args]);
