namespace WitOS.Dev.Images;

/// <summary>
/// One PAL-profile guest fixture from build/fixtures/&lt;name&gt;.json: what to compile and link, and how the image is
/// embedded into the kernel self-tests and recorded as evidence.
/// </summary>
/// <param name="Name">Manifest name.</param>
/// <param name="Description">What the fixture exercises; also the comment of the generated header.</param>
/// <param name="Image">File name of the linked PE image.</param>
/// <param name="ObjectPrefix">Prefix of the fixture's own object files.</param>
/// <param name="UpstreamHeaders">Whether sources compile against the pinned upstream NativeAOT headers.</param>
/// <param name="Defines">Preprocessor symbols added to the PAL profile.</param>
/// <param name="Assembly">MASM sources assembled into objects named after the file.</param>
/// <param name="Sources">Repository-relative C and C++ sources in link order.</param>
/// <param name="FunctionSections">Sources compiled with one section per function (/Gy).</param>
/// <param name="Shared">Objects built earlier in the same output directory and linked before the fixture's own.</param>
/// <param name="Header">Generated C header that embeds the image.</param>
/// <param name="Symbol">Array name of the embedded image.</param>
/// <param name="Report">Evidence report written next to the image.</param>
/// <param name="Scope">What the evidence does and does not prove.</param>
/// <param name="Inputs">Further repository files hashed into the evidence.</param>
/// <param name="Summary">Short result printed after the build.</param>
internal sealed record PalFixture(string Name, string Description, string Image, string ObjectPrefix, bool UpstreamHeaders,
    string[] Defines, string[] Assembly, string[] Sources, string[] FunctionSections, string[] Shared, string Header,
    string Symbol, string Report, string Scope, string[] Inputs, string Summary);
