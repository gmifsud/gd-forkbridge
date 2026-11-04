@{
    # -------------------------------------------------------------------------
    # Backend (QiSL) <-> ISL-Backend branch alignment - configuration.
    # Canonical D: paths only. C:\Repos is an NTFS junction; never use it here.
    # -------------------------------------------------------------------------
    Backend = @{
        Root         = 'D:\Repos\Backend'
        WorktreeRoot = 'D:\Repos\Backend.worktrees'
        BaseRef      = 'origin/migrations/24326-upgrade-angular-to-version-17'
        NewBranch    = 'migrations/24326-isl-alignment'
        TargetBranch = 'Development'
        MergeRefs    = @()
        # Blobs reachable from these refs count as "Backend once held this exact content"
        # (used to recognise stale files the fork kept after Backend deleted them).
        HistoryRefs  = @('origin/Development', 'origin/feature/sync-ebs-repo')
        # QiSL is primary: plan.md tags each promotion/merge with whether Backend or ISL matches this ref.
        ReferenceRef = 'origin/Development'
        AzureProject = 'QiSL'
        AzureRepo    = 'Backend'
    }
    Isl = @{
        Root         = 'D:\Repos\ISL\ISL-Backend'
        WorktreeRoot = 'D:\Repos\ISL\ISL-Backend.worktrees'
        # ISL has no migrations/24326-* branch; its Angular 17 counterpart is 24404.
        BaseRef      = 'origin/migration/24404-plt-ng17-fork-be'
        NewBranch    = 'migration/24404-qisl-alignment'
        TargetBranch = 'development'
        # Merged into the new ISL branch before planning (module-types enum fix, reverts 4412b4f48's overloads).
        MergeRefs    = @('origin/fix/plt-core-module-types-enum')
        HistoryRefs  = @()
        AzureProject = 'EBS (Platform)'
        AzureRepo    = 'Backend'
    }
    AzureOrg = 'https://dev.azure.com/ISLMalta'

    # Backend commit the fork last synchronised from = 3-way merge base for shared paths.
    # ef3ce9136 (feature/sync-ebs-repo, 2026-10-21 12:24) is the source of ISL's overlay 385c4f8ef:
    # 6052 identical / 12 differing scoped paths, best match of all candidates.
    # After both alignment PRs merge, replace with the Backend commit recorded in sync-ledger.json.
    
    #SyncBase = 'ef3ce913633f5a0bd0accb04fdafddb180ae4cd0'
    SyncBase ='720bbf74f3ad9819779d9d453ae00b024a1c3124'

    # Scratch repo, plans, patches, transcripts, ledger. Outside both repositories.
    StateDir = 'D:\ForkBridge\state\branch-alignment'

    # Shared paths reconciled by this tool (folders or single files, repo-relative, '/' separators).
    # QiSL.App.Entity is excluded on purpose (fa37e1d98); it is byte-identical today.
    # global.json pins the SDK (Backend 4bcac609c); both repos must build with the same one.
    Scope = @('QiSL.Fabric', 'QiSL.App.WF', 'Directory.Packages.props', 'global.json')

    # Excluded entirely - never read, never written. Hosts are per-product composition roots
    # (module wiring + environment config), not shared code: ISL's Web.Host wires ISL modules.
    Protected = @(
        'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.Host'
        # .Client proxy libraries: not reconciled (pasted into ISL by mistake; each repo keeps its own).
        'QiSL.App.WF/src/QiSL.App.WF.Application.Client'
        'QiSL.Fabric/BaseTemplate.0/aspnet-core/src/QiSL.App.BaseTemplate.Application.Client'
        'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Application.Client'
        'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ISL.Host'
        'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ThirdParty.ISL.Host'
        # ThirdParty.Host is a per-product host too: ISL's wires SalesPortal + WRH (2026-10-27 review).
        'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ThirdParty.Host'
    )

    # ISL-only files inside shared folders that must never be promoted to Backend (-like patterns).
    ForkOwned = @(
        'QiSL.Fabric/ISL.Overrides/*'
        'QiSL.Fabric/Directory.Build.targets'
        'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.ExternalService/Config/*'
        '*ISL.sln'
        '*/_Fork/*'
        '*.Fork.cs'
    )

    # Extensions where whitespace/BOM-only differences carry no meaning (format-only => Backend formatting kept).
    FormatInsensitive = @('.cs', '.csproj', '.props', '.targets', '.json', '.config', '.resx', '.xml')

    # Per-path decisions that beat the default rules (first match wins, -like patterns).
    Overrides = @(
        @{
            Pattern  = '*/packages.lock.json'
            Decision = 'KEEP_BOTH'
            Reason   = 'Derived from Directory.Packages.props; regenerate per repo with dotnet restore --force-evaluate'
        }
        @{
            Pattern  = 'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/Migrations/Seed/Workflows/CustomerOnboarding/CustomerOnboardingWorkflowStandingDataCreator.cs'
            Decision = 'KEEP_BOTH'
            Reason   = 'ISL uses ModuleTypes.CUSTOMER (ISL.Overrides enum, absent in Backend; value differs from the "Customer" literal). Resolve with the WF fork hook (patches 01/02), not by copying.'
        }
        # 2026-10-27 review of plan 20261027-072032: ISL-specific content that must not reach Backend.
        @{
            Pattern  = 'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.EntityFrameworkCore/Migrations/Seed/SharedKernelSeederService.cs'
            Decision = 'KEEP_BOTH'
            Reason   = 'ISL calls DefaultLockAssociationDataCreator (ISL-only, seeds MTL FuelTransaction locks); Backend does not compile with it. Move the seed into the MTL module.'
        }
        @{
            Pattern  = 'QiSL.Fabric/BaseTemplate.0/aspnet-core/src/QiSL.App.BaseTemplate.Core/Authorization/BaseTemplateAppAuthProviderBase.cs'
            Decision = 'KEEP_BOTH'
            Reason   = 'ISL compat shim (addPermissions / old PermissionData) for module DLLs built against the former API'
        }
        @{
            Pattern  = 'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.Migrator/appsettings.json'
            Decision = 'KEEP_BOTH'
            Reason   = 'Environment config (connection string, Recaptcha) differs per product'
        }
        @{
            Pattern  = 'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.ExternalService/Config/AutoLine/ExternalServicesConfig.json'
            Decision = 'KEEP_BOTH'
            Reason   = 'ISL dev endpoint (panama.isl.dom)'
        }
        @{
            Pattern  = 'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.Job.Engine.Console/Program.cs'
            Decision = 'KEEP_BOTH'
            Reason   = 'Developer test harness (AxelRecurringJob)'
        }
    )

    # Applied to both sides when Directory.Packages.props is unioned. Other version differences are reported only.
    PackageVersionOverrides = @{
        'Microsoft.NET.Test.Sdk' = '17.4.1'
    }

    # Post-apply guards: pattern must NOT match in the given side (git grep -E on *.cs).
    Guards = @(
        @{ Name = 'string module overloads (4412b4f48)'; Side = 'Both'; Pattern = ': base\(options, "|ConfigureModularStatuses\(tenantId, "|GetLobModule\(tenantId, "' }
        # ISL composes ModuleTypes (upstream + fork delta); fork ordinals move when upstream appends. Names only.
        @{ Name = 'ModuleTypes used by ordinal'; Side = 'Both'; Pattern = '\(int\) ?ModuleTypes\.|\(ModuleTypes\) ?[0-9]' }
    )

    # Targeted builds for 04-Test-Alignment.ps1 -Build (relative to each worktree).
    Builds = @{
        Backend = @(
            'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.EntityFrameworkCore/QiSL.Platform.SharedKernel.EntityFrameworkCore.csproj'
            'QiSL.Fabric/BaseTemplate.0/aspnet-core/src/QiSL.App.BaseTemplate.EntityFrameworkCore/QiSL.App.BaseTemplate.EntityFrameworkCore.csproj'
            'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/QiSL.App.WF.EntityFrameworkCore.csproj'
            'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.Host/QiSL.Platform.Web.Host.csproj|Release'
        )
        Isl = @(
            'QiSL.Fabric/BaseTemplate.0/aspnet-core/src/QiSL.App.BaseTemplate.EntityFrameworkCore/QiSL.App.BaseTemplate.EntityFrameworkCore.csproj'
            'QiSL.App.Customer/src/QiSL.App.Customer.EntityFrameworkCore/QiSL.App.Customer.EntityFrameworkCore.csproj'
            'QiSL.App.WRH/src/QiSL.App.WRH.EntityFrameworkCore/QiSL.App.WRH.EntityFrameworkCore.csproj'
            'QiSL.App.EyeConnect/src/QiSL.App.EyeConnect.EntityFrameworkCore/QiSL.App.EyeConnect.EntityFrameworkCore.csproj'
            'QiSL.App.MTL/src/QiSL.App.MTL.EntityFrameworkCore/QiSL.App.MTL.EntityFrameworkCore.csproj'
            'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/QiSL.App.WF.EntityFrameworkCore.csproj'
            # Module Web.Core DLLs reach lib/ only via PostBuild; Customer's is gitignored (never committed), so a fresh worktree lacks it.
            'QiSL.App.Customer/src/QiSL.App.Customer.Web.Core/QiSL.App.Customer.Web.Core.csproj'
            'QiSL.App.EyeConnect/src/QiSL.App.EyeConnect.Web.Core/QiSL.App.EyeConnect.Web.Core.csproj'
            'QiSL.App.MTL/src/QiSL.App.MTL.Web.Core/QiSL.App.MTL.Web.Core.csproj'
            'QiSL.App.WRH/src/QiSL.App.WRH.Web.Core/QiSL.App.WRH.Web.Core.csproj'
            'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ISL.Host/QiSL.Platform.Web.ISL.Host.csproj|Development'
        )
    }

    # Branches 05-Publish-Alignment.ps1 refuses to push to, whatever the config says.
    NeverPush = @('Development', 'development', 'master', 'main',
                  'migrations/24326-upgrade-angular-to-version-17', 'migration/24404-plt-ng17-fork-be')
}
