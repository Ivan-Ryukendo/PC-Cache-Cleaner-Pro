namespace CleanPC.Pro
{
    /// <summary>Anything with a row checkbox; lets the window toggle the selected rows with the Space key.</summary>
    public interface ITickable { bool IsChecked { get; set; } }
}
