namespace StretchCordNG.Models
{
    /// <summary>A Windows render (playback) endpoint.</summary>
    public sealed class OutputDevice
    {
        /// <summary>Null means the Windows default render device.</summary>
        public string? Id { get; }
        public string Name { get; }
        public bool IsDefault { get; }

        /// <summary>
        /// True when Windows reports active audio sessions on this endpoint, i.e. something
        /// is actually coming out of it right now. Used to pick a "spare" device automatically.
        /// </summary>
        public bool HasPlayingSessions { get; set; }

        public OutputDevice(string? id, string name, bool isDefault = false)
        {
            Id = id;
            Name = name;
            IsDefault = isDefault;
        }

        public string ShortName
        {
            get
            {
                var name = Name;
                var paren = name.IndexOf('(');
                if (paren > 0 && paren < name.Length - 1)
                {
                    var head = name[..paren].Trim();
                    if (head.Length > 0 && head.Length < 24) return head;
                }
                return name.Length > 42 ? name[..42] + "…" : name;
            }
        }

        public override string ToString() => IsDefault ? $"{ShortName} (default)" : ShortName;
    }
}
