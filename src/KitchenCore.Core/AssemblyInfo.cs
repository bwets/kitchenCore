using System.Runtime.CompilerServices;

// The tests assert that a raw token never reaches disk, which means checking the
// stored value really is the hash of it.
[assembly: InternalsVisibleTo("KitchenCore.Tests")]
