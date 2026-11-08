using System.Runtime.CompilerServices;

// Declared in source rather than in the project file, because Unity compiles this folder from source without the project, and
// Mono refuses a call into an internal member that the compiled assembly does not grant. The directory, built as its own DLL and
// run inside an editor, reads the clock.
[assembly: InternalsVisibleTo("Newfarm.Server")]
[assembly: InternalsVisibleTo("Newfarm.Tests")]
