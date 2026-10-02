namespace WitOS.HostBindingDependency;

public static class Dependency
{
    public static int Compute() => new[] { 2, 3, 5 }.Select(value => value * value).Sum() + 4;
}
