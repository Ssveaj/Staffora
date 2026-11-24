using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Test.Unit.Workflow;

public class PortalUserWorkflowTests
{
    private static readonly Workflow<UserState, ApplicationUser> Users = WorkflowRegistry.Default.Get<UserState, ApplicationUser>();

    [Fact]
    public void Each_edition_reaches_only_its_own_steps()
    {
        var portal = Users.Transitions.Where(step => ApplicationUserWorkflowAttribute.PortalSteps.Contains(step.Key)).ToList();
        Assert.Equal(ApplicationUserWorkflowAttribute.PortalSteps.Order(), portal.Select(step => step.Key).Order());
        Assert.All(portal, step => Assert.Equal(RolePermission.CustomerUsersManage, step.RequiredPermission));
        Assert.All(Users.Transitions.Except(portal).Where(step => !step.IsSystemDriven), step => Assert.True(step.RequiredPermission is RolePermission.UsersCreate or RolePermission.UsersEdit, step.Key));

        Assert.True(RolePermissions.IsPortal(RolePermission.CustomerUsersManage));
        Assert.False(RolePermissions.IsPortal(RolePermission.UsersCreate) || RolePermissions.IsPortal(RolePermission.UsersEdit));
    }

    [Fact]
    public void The_portals_creates_give_their_role_and_its_other_steps_mirror_the_staffing_companys_and_take_no_edits()
    {
        Assert.Equal(RolePresets.Orderer, ApplicationUserWorkflowAttribute.PortalRoleOf(ApplicationUserWorkflowAttribute.PortalCreateOrdererStep));
        Assert.Equal(RolePresets.CustomerAdmin, ApplicationUserWorkflowAttribute.PortalRoleOf(ApplicationUserWorkflowAttribute.PortalCreateAdministratorStep));
        Assert.Null(ApplicationUserWorkflowAttribute.PortalRoleOf(ApplicationUserWorkflowAttribute.CreateStep));
        Assert.All(
            [ApplicationUserWorkflowAttribute.PortalCreateOrdererStep, ApplicationUserWorkflowAttribute.PortalCreateAdministratorStep],
            key => Assert.Equal((UserState.Create, UserState.Active, 5), (Users.GetTransition(key).From, Users.GetTransition(key).To, Users.GetTransition(key).Conditions.Count)));

        foreach (var (portal, own) in new[]
        {
            (ApplicationUserWorkflowAttribute.PortalNewPasswordStep, ApplicationUserWorkflowAttribute.NewPasswordStep),
            (ApplicationUserWorkflowAttribute.PortalDeactivateStep, ApplicationUserWorkflowAttribute.DeactivateStep),
            (ApplicationUserWorkflowAttribute.PortalReactivateStep, ApplicationUserWorkflowAttribute.ReactivateStep),
        })
        {
            var (theirs, ours) = (Users.GetTransition(portal), Users.GetTransition(own));
            Assert.Equal((ours.From, ours.To, ours.Label, ours.ConfirmationMessage, ours.Conditions.Count, ours.Actions.Count), (theirs.From, theirs.To, theirs.Label, theirs.ConfirmationMessage, theirs.Conditions.Count, theirs.Actions.Count));
            Assert.Equal(ours.Conditions.Skip(1).Select(condition => condition.Description), theirs.Conditions.Skip(1).Select(condition => condition.Description));
            Assert.Equal("Only a user of this organization changes its users", theirs.Conditions[0].Description);
            Assert.Equal(
                own == ApplicationUserWorkflowAttribute.NewPasswordStep ? "Only a user of this organization or the platform owner changes this" : theirs.Conditions[0].Description,
                ours.Conditions[0].Description);
            Assert.True(theirs.TakesNoEdits);
        }
    }
}
