# Franthropy build dependency

The exact package pin is owned by `eng/Franthropy.Version.props`.
Populate the private local feed before restoring. Its default is
`$(LOCALAPPDATA)/Franthropy/packages`; override with
`-p:FranthropyFeed=<feed-directory>`. Packages are not published publicly.

Release builds use packages without a source checkout. Paired development
requires both `-p:FranthropySource=Local` and
`-p:FranthropyRoot=<absolute-source-root>` on dotnet build/test.
Missing roots and unknown modes fail explicitly. Rebuild in package mode
before release packaging.

Core remains a source reference pinned by `eng/craft-architect.ref`. Check out
that exact commit and pass `-p:CraftArchitectCoreProject=<Core.csproj>` when it
is not in the default sibling location. Core itself uses the same Franthropy
package pin. CI checks out both exact source pins and explicitly selects Local
mode; it downloads build references, but never starts a game or launcher.

Old `franthropy.ref` or `Franthropy.commit` files, where present, are retained
only for rollback and no longer select the build dependency.
