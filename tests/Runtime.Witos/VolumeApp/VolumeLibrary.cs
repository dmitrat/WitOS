// The application's own library on the volume (plan step D5.3), which CoreCLR loads from the application's directory.
public static class VolumeLibrary
{
    public static string Answer(string greeting) => $"{greeting}, answered by {typeof(VolumeLibrary).Assembly.GetName().Name}";
}
