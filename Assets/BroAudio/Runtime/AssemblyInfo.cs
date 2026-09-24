using System.Runtime.CompilerServices;

// The PlayMode regression suite (Assets/Tests/Tests.asmdef) reads SoundManager's live player list through
// its internal accessor instead of reflecting a private member, so a refactor of the pool fails to compile
// rather than failing every PlayMode test at run time. Nothing outside the suite depends on this.
[assembly: InternalsVisibleTo("Tests")]