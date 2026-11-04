@{
    # -------------------------------------------------------------------------
    # Frontend (QiSL) <-> ISL-Frontend branch alignment - configuration.
    # Canonical D: paths only. C:\Repos is an NTFS junction; never use it here.
    # -------------------------------------------------------------------------
    Frontend = @{
        Root         = 'D:\Repos\Frontend'
        WorktreeRoot = 'D:\Repos\Frontend.worktrees'
        # Branch checked out in D:\Repos\Frontend.worktrees\feature\configuration-cache-synchronisation
        # (the folder name predates the branch name). fa65d310d on 2026-10-27, local == origin.
        BaseRef      = 'origin/migration/configuration-cache-synchronisation-ng17'
        NewBranch    = 'migration/configuration-cache-synchronisation-ng17-isl-alignment'
        TargetBranch = 'migration/configuration-cache-synchronisation-ng17'
        # Optional: Worktree = '<path>' overrides WorktreeRoot\NewBranch. Pointing NewBranch + Worktree at an
        # existing branch/worktree aligns it in place (01 then only verifies it); the default is a new branch.
        MergeRefs    = @()
        # Blobs reachable from these refs count as "Frontend once held this exact content":
        # ISL_BEHIND (ISL holds an older Frontend version) and ISL_STALE (Frontend deleted it).
        HistoryRefs  = @('origin/Development', 'origin/migrations/24326-upgrade-angular-to-version-17')
        # QiSL is primary: plan.md tags each promotion/merge with whether Frontend or ISL matches this ref.
        ReferenceRef = 'origin/migrations/24326-upgrade-angular-to-version-17'
        AzureProject = 'QiSL'
        AzureRepo    = 'Frontend'
    }
    Isl = @{
        Root         = 'D:\Repos\ISL\ISL-Frontend'
        WorktreeRoot = 'D:\Repos\ISL\ISL-Frontend.worktrees'
        # ISL's Angular 17 counterpart of 24326 is 24403 (the -be branch of the same number is unrelated here).
        BaseRef      = 'origin/migration/24403-plt-ng17-fork-fe'
        NewBranch    = 'migration/24403-qisl-fe-alignment'
        TargetBranch = 'migration/24403-plt-ng17-fork-fe'
        MergeRefs    = @()
        HistoryRefs  = @()
        AzureProject = 'EBS (Platform)'
        AzureRepo    = 'Frontend'
    }
    AzureOrg = 'https://dev.azure.com/ISLMalta'

    # 3-way merge base for the shared scope = git merge-base of the two branches (c56a755 "#23707 Fix for
    # Nswag Warnings", 2026-08-06). Unlike the Backend (one overlay commit, ef3ce9136), ISL-Frontend takes
    # Frontend changes commit by commit (scripts/Fork/FromUpstreamCommit.ps1, no ancestry), so no single
    # Frontend commit is its source: the nearest candidate (27134019f) still differs on 444 scoped paths, and
    # using it would make every Frontend change ISL skipped look like an ISL revert. The ISL_BEHIND rule in
    # 02 absorbs the per-commit imports instead (simulated 2026-10-27: 207 paths).
    # Reproduce: git -C D:\Repos\ISL\ISL-Frontend merge-base origin/migration/24403-plt-ng17-fork-fe fa65d310d
    #            (with D:\Repos\Frontend objects reachable), or rerun 02 -Preview with another candidate.
    # After both alignment PRs merge, replace with nextSyncBase from StateDir\sync-ledger.json.
    SyncBase = 'c56a755001d612865934fb79ebc5b2a26fcf717c'

    # Scratch repo, plans, patches, transcripts, ledger. Outside both repositories.
    StateDir = 'D:\ForkBridge\state\frontend-alignment'

    # Shared paths reconciled by this tool (folders or single files, repo-relative, '/' separators).
    # Same folders ISL's own scripts/Fork/FromUpstream*.ps1 import. Repository-root files (package.json
    # workspaces, lockfiles, *.sln, pipelines) are per-product composition and stay out of scope.
    Scope = @('abp-zero-template', 'qisl-client-ng-fabric', 'qisl-client-ng-mod-wf')

    # Excluded entirely - never read, never written.
    Protected = @(
        # ISL-protected paths (scripts/Fork/FromUpstream.Common.psm1): ISL navigation + ISL helpers.
        'qisl-client-ng-fabric/src/helpers/isl'
        'qisl-client-ng-fabric/src/modules/shared/layout/nav/qisl-shared-navigation.service.ts'
        'qisl-client-ng-fabric/src/modules/shared/layout/nav/routes/shared/navigation-route-context.ts'
        'qisl-client-ng-fabric/src/shared/services/registry/isl.type.registry.module.ts'
        'abp-zero-template/src/app/shared/layout/nav/qisl-navigation.registry.ts'
        # Environment configuration (per product / per deployment).
        'abp-zero-template/src/assets/appconfig.json'
        'abp-zero-template/src/assets/appconfig.docker.json'
        'abp-zero-template/src/assets/appconfig.k8s.json'
        'abp-zero-template/src/assets/appconfig.production.json'
        # NSwag output, generated per product from its own backend modules (nswag/refresh.ps1).
        # Each repo regenerates after the Backend alignment; never reconciled by copying.
        'abp-zero-template/.precompiled-proxies'
        'abp-zero-template/src/shared/service-proxies'
    )

    # ISL-only files inside shared folders that must never be promoted to Frontend (-like patterns).
    ForkOwned = @(
        'qisl-client-ng-fabric/src/modules/shared/layout/nav/routes/isl-routes/*'
        'abp-zero-template/gulp/isl/*'
        'abp-zero-template/src/assets/common/styles/themes/isl/*'
        'abp-zero-template/src/assets/metronic/themes/isl/*'
        '*/_Fork/*'
        '*.fork.ts'
    )

    # Extensions where whitespace/BOM-only differences carry no meaning (format-only => Frontend formatting kept).
    FormatInsensitive = @('.ts', '.html', '.scss', '.css', '.less', '.json', '.js')

    # Per-path decisions that beat the default rules (first match wins, -like patterns).
    # 02 appends the default decision it replaced and whether a clean 3-way merge exists (then MERGED is an option).
    Overrides = @(
        @{ Pattern = 'abp-zero-template/angular.json';                    Decision = 'KEEP_BOTH'; Reason = 'host composition root: per-product projects, assets and styles' }
        @{ Pattern = '*/src/tsconfig.json';                               Decision = 'KEEP_BOTH'; Reason = 'module path aliases and node_modules layout differ per product' }
        @{ Pattern = 'abp-zero-template/tsconfig.proxies.json';           Decision = 'KEEP_BOTH'; Reason = 'lists the product''s own segregated proxies' }
        @{ Pattern = 'abp-zero-template/src/app/app-routing.module.ts';   Decision = 'KEEP_BOTH'; Reason = 'lazy routes to the product''s own modules' }
        @{ Pattern = 'abp-zero-template/nswag/qisl.module.names.json';    Decision = 'KEEP_BOTH'; Reason = 'module list for proxy generation, per product' }
        @{ Pattern = 'abp-zero-template/src/index.html';                  Decision = 'KEEP_BOTH'; Reason = 'product branding (title, og:*, favicon)' }
        @{ Pattern = 'abp-zero-template/src/environments/*';              Decision = 'KEEP_BOTH'; Reason = 'environment configuration (Sentry DSN etc.)' }
        @{ Pattern = 'qisl-client-ng-fabric/src/shared/services/registry/*.module.registry.ts'; Decision = 'KEEP_BOTH'; Reason = 'lazy module registry, per product' }
    )

    # package.json files whose dependency sections are unioned (add-only, like Directory.Packages.props in the
    # Backend tool). Each side gains the other's missing entries; removals never propagate; version differences
    # are reported only, except those in PackageVersionOverrides. Everything else in the file stays per side.
    PackageFiles    = @('*/package.json')
    PackageSections = @('dependencies', 'devDependencies', 'peerDependencies')
    PackageVersionOverrides = @{}

    # Post-apply guards: Pattern (git grep -E) must NOT match in the given side, inside Scope, in files
    # matching Include. Fabric/WF must not learn about ISL-only modules.
    Guards = @(
        @{
            Name    = 'ISL module references in Frontend'
            Side    = 'Frontend'
            Pattern = 'qisl-client-ng-mod-(customer|eyeconnect|mtl|wrh)|@qisl(mod)?(customer|eyeconnect|mtl|wrh)/'
            Include = @('*.ts', '*.json', '*.html')
        }
    )

    # Targeted builds for 04-Test-Alignment.ps1 -Build (run in each worktree root; Exe + Args, no shell).
    # A fresh worktree has no node_modules, so install first (slow; uses the yarn cache).
    Builds = @{
        Frontend = @(
            @{ Name = 'install'; Exe = 'yarn'; Args = @('install', '--frozen-lockfile', '--network-timeout', '600000') }
            @{ Name = 'build-dev'; Exe = 'yarn'; Args = @('build:dev') }
        )
        Isl = @(
            @{ Name = 'install'; Exe = 'yarn'; Args = @('install', '--frozen-lockfile', '--network-timeout', '600000') }
            @{ Name = 'build-dev'; Exe = 'yarn'; Args = @('build:dev') }
        )
    }

    # Branches 05-Publish-Alignment.ps1 refuses to push to, whatever the config says.
    NeverPush = @('Development', 'development', 'master', 'main',
                  'migration/configuration-cache-synchronisation-ng17', 'migrations/24326-upgrade-angular-to-version-17',
                  'migration/24403-plt-ng17-fork-fe')
}
