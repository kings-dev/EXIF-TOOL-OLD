namespace Hui_WPF.utils
{
    public class DirectoryRule
    {
        public string Prefix { get; set; }
        public int Count { get; set; }
        public bool Recursive { get; set; }
        public List<DirectoryRule> SubRules { get; set; } = new();
        public override string ToString() => $"{Prefix} ×{Count} {(Recursive ? "[递归]" : "")}";
    }
}
