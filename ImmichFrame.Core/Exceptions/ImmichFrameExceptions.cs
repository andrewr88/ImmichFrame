namespace ImmichFrame.Core.Exceptions
{
    public class ImmichFrameException : Exception
    {
        public ImmichFrameException() { }
        public ImmichFrameException(string? message) : base(message) { }
        public ImmichFrameException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    public class AssetNotFoundException : ImmichFrameException
    {
        public AssetNotFoundException() : base() { }
        public AssetNotFoundException(string message) : base(message) { }
        public AssetNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class AlbumNotFoundException : ImmichFrameException
    {
        public AlbumNotFoundException() : base() { }
        public AlbumNotFoundException(string message) : base(message) { }
        public AlbumNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
    public class PersonNotFoundException : ImmichFrameException
    {
        public PersonNotFoundException() : base() { }
        public PersonNotFoundException(string message) : base(message) { }
        public PersonNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
    public class MemoryNotFoundException : ImmichFrameException
    {
        public MemoryNotFoundException() : base() { }
        public MemoryNotFoundException(string message) : base(message) { }
        public MemoryNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
    public class ProfileNotFoundException : ImmichFrameException
    {
        public ProfileNotFoundException() : base() { }
        public ProfileNotFoundException(string message) : base(message) { }
        public ProfileNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
    /// <summary>
    /// A configuration save that was refused before anything was written, for a reason the operator
    /// can act on: the file changed underneath the editor, there is no file to write, the file is in
    /// the old schema, or the directory is not writable. Distinct from
    /// <see cref="SettingsNotValidException"/>, which means the configuration itself is wrong.
    /// </summary>
    public class ConfigSaveRefusedException : ImmichFrameException
    {
        public ConfigSaveRefusedException() : base() { }
        public ConfigSaveRefusedException(string message) : base(message) { }
        public ConfigSaveRefusedException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class SettingsNotValidException : ImmichFrameException
    {
        public SettingsNotValidException() : base() { }
        public SettingsNotValidException(string message) : base(message) { }
        public SettingsNotValidException(string message, Exception innerException) : base(message, innerException) { }
    }
}
