using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Statistics;
using NUnit.Framework;

namespace RepresentativeLibraries;

/// <summary>
/// Math.NET Numerics with its managed provider: a linear system, a decomposition, a Fourier transform, statistics and a
/// fit.
/// </summary>
[TestFixture]
public sealed class MathNetTests
{
    [Test]
    public void LinearSystemTest()
    {
        var a = Matrix<double>.Build.DenseOfArray(new double[,] { { 3, 2, -1 }, { 2, -2, 4 }, { -1, 0.5, -1 } });
        var b = Vector<double>.Build.Dense([1, -2, 0]);

        var x = a.Solve(b);

        Assert.That(x.ToArray(), Is.EqualTo(new[] { 1.0, -2.0, -2.0 }).Within(1e-9));
    }

    [Test]
    public void DecompositionTest()
    {
        var a = Matrix<double>.Build.Random(40, 40, 7);
        var qr = a.QR();

        Assert.That((qr.Q * qr.R - a).FrobeniusNorm(), Is.LessThan(1e-9));
        Assert.That(a.Determinant(), Is.EqualTo(qr.R.Diagonal().Aggregate(1.0, (product, value) => product * value) * qr.Q.Determinant()).Within(1e-6).Percent);
    }

    [Test]
    public void FourierTransformTest()
    {
        var samples = Enumerable.Range(0, 64).Select(i => new System.Numerics.Complex(Math.Sin(2 * Math.PI * 4 * i / 64), 0)).ToArray();

        Fourier.Forward(samples, FourierOptions.Matlab);
        var peak = samples.Take(32).Select((value, index) => (value.Magnitude, index)).Max().index;

        Assert.That(peak, Is.EqualTo(4));
    }

    [Test]
    public void StatisticsAndFitTest()
    {
        var x = Enumerable.Range(0, 20).Select(i => (double)i).ToArray();
        var y = x.Select(value => 3 * value + 2).ToArray();

        var (intercept, slope) = Fit.Line(x, y);

        Assert.That((intercept, slope), Is.EqualTo((2.0, 3.0)).Using<(double, double)>((left, right) =>
            Math.Abs(left.Item1 - right.Item1) < 1e-9 && Math.Abs(left.Item2 - right.Item2) < 1e-9));
        Assert.That(y.Mean(), Is.EqualTo(30.5).Within(1e-9));
        Assert.That(SpecialFunctions.Gamma(5), Is.EqualTo(24).Within(1e-9));
    }
}
