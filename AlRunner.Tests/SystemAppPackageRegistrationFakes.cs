// Fakes for SystemAppPackageRegistrationFailureTests (#3581).
// ── The fake SystemPackage types ─────────────────────────────────────────────────────────
//
// Production selects the type by `t.Name == "SystemPackage"`, so every fake standing in for it
// must be NAMED exactly that — which is why they live in sibling namespaces rather than as
// nested types of the test class. A nested type's Name is its simple name, so four nested
// classes could not all be called SystemPackage; the namespace is what keeps them distinct to
// the compiler while keeping the one property production switches on identical. The runner
// never resolves these by namespace.

namespace AlRunner.Tests.SystemAppFakes.NoGetStream
{
    /// <summary>SystemPackage without GetPackageStream. It declares a DIFFERENT static method so
    /// "the type has no members at all" is not what makes the arm pass.</summary>
    internal sealed class SystemPackage
    {
        public static int Unrelated() => 0;
    }
}

namespace AlRunner.Tests.SystemAppFakes.NullStream
{
    /// <summary>GetPackageStream is present and answers null — the case the null-forgiving `!`
    /// used to turn into an NRE inside the swallowing catch.</summary>
    internal sealed class SystemPackage
    {
        public static System.IO.Stream? GetPackageStream() => null;
    }
}

namespace AlRunner.Tests.SystemAppFakes.ThrowingRead
{
    /// <summary>A stream whose read throws — standing in for a full disk, a permission fault or
    /// a truncated embedded resource during extraction.</summary>
    internal sealed class SystemPackage
    {
        public static System.IO.Stream GetPackageStream()
            => new AlRunner.Tests.SystemAppFakes.ThrowingStream();
    }
}

namespace AlRunner.Tests.SystemAppFakes.Healthy
{
    /// <summary>The positive control: a package that extracts cleanly.</summary>
    internal sealed class SystemPackage
    {
        /// <summary>Not a real NAVX zip — nothing in the helper parses it; the registration
        /// callback is what would, and the arms substitute for it.</summary>
        internal static readonly byte[] Payload = { 0x50, 0x4B, 0x03, 0x04, 0x11, 0x22, 0x33 };

        public static System.IO.Stream GetPackageStream()
            => new System.IO.MemoryStream(Payload, writable: false);
    }
}

namespace AlRunner.Tests.SystemAppFakes
{
    internal sealed class ThrowingStream : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new System.NotSupportedException();
        public override long Position { get => 0; set => throw new System.NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => throw new System.IO.IOException("the package stream refused");
        public override long Seek(long offset, System.IO.SeekOrigin origin)
            => throw new System.NotSupportedException();
        public override void SetLength(long value) => throw new System.NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new System.NotSupportedException();
    }
}
