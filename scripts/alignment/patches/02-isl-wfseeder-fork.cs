// ISL-Backend only. Path: QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/Migrations/Seed/WFSeederService.Fork.cs
// This file never exists upstream, so scripts/Fork/FromUpstreamWF.ps1 (merge-tree) always preserves it.
using Abp.Domain.Uow;
using Abp.Localization;
using QiSL.App.BaseTemplate.EntityFrameworkCore;
using QiSL.App.WF.EntityFrameworkCore;
using QiSL.App.WF.Migrations.Seed.Workflows.CustomerOnboarding;

namespace QiSL.App.WF.Migrations.Seed
{
    public partial class WFSeederService
    {
        partial void SeedForkWorkflows(
            IUnitOfWorkManager unitOfWorkManager,
            ILocalizationManager localizationManager,
            BaseTemplateDbContext baseTemplateDbContext,
            WFDbContext context,
            int? tenantId)
        {
            new CustomerOnboardingWorkflowStandingDataCreator(
                  unitOfWorkManager: unitOfWorkManager,
                  baseTemplateDbContext: baseTemplateDbContext,
                  context: context,
                  localizationManager: localizationManager,
                  localizationSourceName: WFConsts.LocalizationSourceName)
              .CreateForTenant(tenantId);
        }
    }
}
