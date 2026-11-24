using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Staffora.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErrorLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Occurred = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    EventName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ExceptionType = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ExceptionDetail = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RequestPath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsRootTenant = table.Column<bool>(type: "bit", nullable: false),
                    ParentTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.CheckConstraint("CK_Tenants_ParentTenantId_NotSelf", "[ParentTenantId] IS NULL OR [ParentTenantId] <> [Id]");
                    table.ForeignKey(
                        name: "FK_Tenants_Tenants_ParentTenantId",
                        column: x => x.ParentTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    LastSignedIn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false),
                    IsSystemAccount = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUsers_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AspNetUsers_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AspNetUsers_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AspNetUsers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ApplicationUserAttributeDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ValueType = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Options = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationUserAttributeDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationUserAttributeDefinitions_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ApplicationUserAttributeDefinitions_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ApplicationUserAttributeDefinitions_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ApplicationUserAttributeDefinitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommentContainers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Audience = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentContainers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommentContainers_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CommentContainers_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CommentContainers_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ImpersonationAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImpersonatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Started = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Ended = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImpersonationAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImpersonationAudits_AspNetUsers_ImpersonatorId",
                        column: x => x.ImpersonatorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ImpersonationAudits_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ImpersonationAudits_Tenants_TargetTenantId",
                        column: x => x.TargetTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "IntegrationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttempt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Finished = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailure = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationMessages_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IntegrationMessages_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IntegrationMessages_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IntegrationMessages_Tenants_TargetTenantId",
                        column: x => x.TargetTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "IntegrationReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationReceipts_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IntegrationReceipts_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IntegrationReceipts_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "JobAttributeDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Audience = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ValueType = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Options = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAttributeDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobAttributeDefinitions_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobAttributeDefinitions_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobAttributeDefinitions_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobAttributeDefinitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrganizationUnitAttributeDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ValueType = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Options = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationUnitAttributeDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationUnitAttributeDefinitions_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitAttributeDefinitions_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitAttributeDefinitions_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitAttributeDefinitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrganizationUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UnitType = table.Column<int>(type: "int", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationUnits", x => x.Id);
                    table.CheckConstraint("CK_OrganizationUnits_NotOwnParent", "[ParentId] IS NULL OR [ParentId] <> [Id]");
                    table.CheckConstraint("CK_OrganizationUnits_ParentUnlessCompany", "([UnitType] = 0 AND [ParentId] IS NULL) OR ([UnitType] <> 0 AND [ParentId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_OrganizationUnits_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnits_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnits_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnits_OrganizationUnits_ParentId",
                        column: x => x.ParentId,
                        principalTable: "OrganizationUnits",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnits_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OutgoingMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HistoryEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Link = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecipientUserIds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RecipientPermission = table.Column<int>(type: "int", nullable: true),
                    NamedRecipientsHold = table.Column<int>(type: "int", nullable: true),
                    FallbackPermission = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Delivered = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RecipientCount = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttempt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutgoingMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutgoingMessages_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutgoingMessages_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutgoingMessages_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Qualifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Qualifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Qualifications_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Qualifications_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Qualifications_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Resources_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Resources_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Resources_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Resources_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Services",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DefaultSlotCount = table.Column<int>(type: "int", nullable: false),
                    OfferedInPortal = table.Column<bool>(type: "bit", nullable: false),
                    CrossTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services", x => x.Id);
                    table.CheckConstraint("CK_Services_DefaultSlotCount", "[DefaultSlotCount] BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_Services_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Services_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Services_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "StateHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransitionKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FromState = table.Column<int>(type: "int", nullable: false),
                    ToState = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StateHistoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StateHistoryEntries_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StateHistoryEntries_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StateHistoryEntries_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LinkedTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Suppliers_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Suppliers_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Suppliers_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TenantRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsSystemOwner = table.Column<bool>(type: "bit", nullable: false),
                    ApplyResourceMembershipToUser = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantRoles_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantRoles_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantRoles_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantRoles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TenantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantSettings_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantSettings_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantSettings_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WorkPatterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CycleWeeks = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPatterns", x => x.Id);
                    table.CheckConstraint("CK_WorkPatterns_CycleWeeks", "[CycleWeeks] BETWEEN 1 AND 8");
                    table.ForeignKey(
                        name: "FK_WorkPatterns_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkPatterns_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkPatterns_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ApplicationUserAttributes",
                columns: table => new
                {
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityAttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StringValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Int32Value = table.Column<int>(type: "int", nullable: true),
                    DoubleValue = table.Column<double>(type: "float", nullable: true),
                    BooleanValue = table.Column<bool>(type: "bit", nullable: true),
                    DateValue = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationUserAttributes", x => new { x.EntityId, x.EntityAttributeDefinitionId });
                    table.ForeignKey(
                        name: "FK_ApplicationUserAttributes_ApplicationUserAttributeDefinitions_EntityAttributeDefinitionId",
                        column: x => x.EntityAttributeDefinitionId,
                        principalTable: "ApplicationUserAttributeDefinitions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ApplicationUserAttributes_AspNetUsers_EntityId",
                        column: x => x.EntityId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClosedDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FirstDay = table.Column<DateOnly>(type: "date", nullable: false),
                    LastDay = table.Column<DateOnly>(type: "date", nullable: false),
                    OrganizationUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClosedDays", x => x.Id);
                    table.CheckConstraint("CK_ClosedDays_LastNotBeforeFirst", "[LastDay] >= [FirstDay]");
                    table.ForeignKey(
                        name: "FK_ClosedDays_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClosedDays_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClosedDays_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClosedDays_OrganizationUnits_OrganizationUnitId",
                        column: x => x.OrganizationUnitId,
                        principalTable: "OrganizationUnits",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LinkedTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Customers_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Customers_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Customers_OrganizationUnits_DefaultUnitId",
                        column: x => x.DefaultUnitId,
                        principalTable: "OrganizationUnits",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrganizationUnitAttributes",
                columns: table => new
                {
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityAttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StringValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Int32Value = table.Column<int>(type: "int", nullable: true),
                    DoubleValue = table.Column<double>(type: "float", nullable: true),
                    BooleanValue = table.Column<bool>(type: "bit", nullable: true),
                    DateValue = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationUnitAttributes", x => new { x.EntityId, x.EntityAttributeDefinitionId });
                    table.ForeignKey(
                        name: "FK_OrganizationUnitAttributes_OrganizationUnitAttributeDefinitions_EntityAttributeDefinitionId",
                        column: x => x.EntityAttributeDefinitionId,
                        principalTable: "OrganizationUnitAttributeDefinitions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitAttributes_OrganizationUnits_EntityId",
                        column: x => x.EntityId,
                        principalTable: "OrganizationUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoredMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutgoingMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Link = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HistoryEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoredMessages_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StoredMessages_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StoredMessages_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StoredMessages_OutgoingMessages_OutgoingMessageId",
                        column: x => x.OutgoingMessageId,
                        principalTable: "OutgoingMessages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ResourceAbsences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    FirstDay = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    LastDay = table.Column<DateOnly>(type: "date", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceAbsences", x => x.Id);
                    table.CheckConstraint("CK_ResourceAbsences_EndNotMidnight", "[EndTime] IS NULL OR [EndTime] > '00:00:00'");
                    table.CheckConstraint("CK_ResourceAbsences_KnownKind", "[Kind] IN (0, 1)");
                    table.CheckConstraint("CK_ResourceAbsences_LastNotBeforeFirst", "[LastDay] >= [FirstDay]");
                    table.CheckConstraint("CK_ResourceAbsences_TimesInOrder", "[FirstDay] <> [LastDay] OR [StartTime] IS NULL OR [EndTime] IS NULL OR [EndTime] > [StartTime]");
                    table.ForeignKey(
                        name: "FK_ResourceAbsences_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceAbsences_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceAbsences_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceAbsences_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ResourceQualifications",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QualificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceQualifications", x => new { x.TenantId, x.ResourceId, x.QualificationId });
                    table.ForeignKey(
                        name: "FK_ResourceQualifications_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceQualifications_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceQualifications_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceQualifications_Qualifications_QualificationId",
                        column: x => x.QualificationId,
                        principalTable: "Qualifications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceQualifications_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ResourceUnitMemberships",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceUnitMemberships", x => new { x.TenantId, x.ResourceId, x.OrganizationUnitId });
                    table.ForeignKey(
                        name: "FK_ResourceUnitMemberships_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceUnitMemberships_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceUnitMemberships_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceUnitMemberships_OrganizationUnits_OrganizationUnitId",
                        column: x => x.OrganizationUnitId,
                        principalTable: "OrganizationUnits",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourceUnitMemberships_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ServiceQualifications",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QualificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceQualifications", x => new { x.TenantId, x.ServiceId, x.QualificationId });
                    table.CheckConstraint("CK_ServiceQualifications_Level", "[Level] IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_ServiceQualifications_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ServiceQualifications_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ServiceQualifications_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ServiceQualifications_Qualifications_QualificationId",
                        column: x => x.QualificationId,
                        principalTable: "Qualifications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ServiceQualifications_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OfferedServices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkedCrossTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DefaultSlotCount = table.Column<int>(type: "int", nullable: false),
                    Offered = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfferedServices", x => x.Id);
                    table.CheckConstraint("CK_OfferedServices_DefaultSlotCount", "[DefaultSlotCount] BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_OfferedServices_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OfferedServices_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OfferedServices_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OfferedServices_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Schedule = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    PlaceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StreetAddress = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrdererId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CrossTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LinkedCrossTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Orders_PeriodInOrder", "[PeriodEnd] >= [PeriodStart]");
                    table.ForeignKey(
                        name: "FK_Orders_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Orders_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Orders_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Orders_AspNetUsers_OrdererId",
                        column: x => x.OrdererId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Orders_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrganizationUnitUserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationUnitUserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationUnitUserRoles_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitUserRoles_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitUserRoles_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitUserRoles_OrganizationUnits_OrganizationUnitId",
                        column: x => x.OrganizationUnitId,
                        principalTable: "OrganizationUnits",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrganizationUnitUserRoles_TenantRoles_TenantRoleId",
                        column: x => x.TenantRoleId,
                        principalTable: "TenantRoles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TenantRolePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantRolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantRolePermissions_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantRolePermissions_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantRolePermissions_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantRolePermissions_TenantRoles_TenantRoleId",
                        column: x => x.TenantRoleId,
                        principalTable: "TenantRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResourcePatternAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkPatternId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CycleAnchor = table.Column<DateOnly>(type: "date", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourcePatternAssignments", x => x.Id);
                    table.CheckConstraint("CK_ResourcePatternAssignments_AnchorMonday", "DATEDIFF(day, '19000101', [CycleAnchor]) % 7 = 0");
                    table.CheckConstraint("CK_ResourcePatternAssignments_EndNotBeforeStart", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");
                    table.ForeignKey(
                        name: "FK_ResourcePatternAssignments_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourcePatternAssignments_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourcePatternAssignments_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourcePatternAssignments_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ResourcePatternAssignments_WorkPatterns_WorkPatternId",
                        column: x => x.WorkPatternId,
                        principalTable: "WorkPatterns",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WorkPatternSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkPatternId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DayIndex = table.Column<int>(type: "int", nullable: false),
                    Start = table.Column<TimeOnly>(type: "time", nullable: false),
                    End = table.Column<TimeOnly>(type: "time", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPatternSegments", x => x.Id);
                    table.CheckConstraint("CK_WorkPatternSegments_DayIndex", "[DayIndex] >= 0");
                    table.CheckConstraint("CK_WorkPatternSegments_StartBeforeEnd", "[Start] < [End]");
                    table.ForeignKey(
                        name: "FK_WorkPatternSegments_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkPatternSegments_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkPatternSegments_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_WorkPatternSegments_WorkPatterns_WorkPatternId",
                        column: x => x.WorkPatternId,
                        principalTable: "WorkPatterns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LinkedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerContacts_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustomerContacts_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustomerContacts_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustomerContacts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MessageRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoredMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageRecipients_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MessageRecipients_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MessageRecipients_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MessageRecipients_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MessageRecipients_StoredMessages_StoredMessageId",
                        column: x => x.StoredMessageId,
                        principalTable: "StoredMessages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrderLines",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OfferedServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLines", x => new { x.TenantId, x.OrderId, x.OfferedServiceId });
                    table.CheckConstraint("CK_OrderLines_Count", "[Count] BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_OrderLines_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderLines_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderLines_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderLines_OfferedServices_OfferedServiceId",
                        column: x => x.OfferedServiceId,
                        principalTable: "OfferedServices",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderLines_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderStaff",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(201)", maxLength: 201, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderStaff", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderStaff_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderStaff_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderStaff_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OrderStaff_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommentContainerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorType = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TransitionKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CrossTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LinkedCrossTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorSupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Comments_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Comments_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Comments_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Comments_CommentContainers_CommentContainerId",
                        column: x => x.CommentContainerId,
                        principalTable: "CommentContainers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Comments_CustomerContacts_AuthorContactId",
                        column: x => x.AuthorContactId,
                        principalTable: "CustomerContacts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Comments_Suppliers_AuthorSupplierId",
                        column: x => x.AuthorSupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrganizationUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Schedule = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    PlaceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StreetAddress = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AccessNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CoordinatorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CrossTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LinkedCrossTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.CheckConstraint("CK_Jobs_PeriodInOrder", "[PeriodEnd] >= [PeriodStart]");
                    table.CheckConstraint("CK_Jobs_ScheduleTimes", "([Schedule] = 1 AND [StartTime] IS NOT NULL AND [EndTime] IS NOT NULL AND [StartTime] <> [EndTime] AND DATEDIFF(day, [PeriodStart], [PeriodEnd]) <= 1) OR ([Schedule] = 2 AND [StartTime] IS NULL AND [EndTime] IS NULL)");
                    table.ForeignKey(
                        name: "FK_Jobs_AspNetUsers_CoordinatorUserId",
                        column: x => x.CoordinatorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Jobs_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Jobs_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Jobs_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Jobs_CustomerContacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "CustomerContacts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Jobs_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Jobs_OrganizationUnits_OrganizationUnitId",
                        column: x => x.OrganizationUnitId,
                        principalTable: "OrganizationUnits",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "JobAttributes",
                columns: table => new
                {
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityAttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StringValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Int32Value = table.Column<int>(type: "int", nullable: true),
                    DoubleValue = table.Column<double>(type: "float", nullable: true),
                    BooleanValue = table.Column<bool>(type: "bit", nullable: true),
                    DateValue = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAttributes", x => new { x.EntityId, x.EntityAttributeDefinitionId });
                    table.ForeignKey(
                        name: "FK_JobAttributes_JobAttributeDefinitions_EntityAttributeDefinitionId",
                        column: x => x.EntityAttributeDefinitionId,
                        principalTable: "JobAttributeDefinitions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobAttributes_Jobs_EntityId",
                        column: x => x.EntityId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobSlots_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobSlots_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobSlots_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobSlots_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobSlots_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "JobTimeSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Weekday = table.Column<int>(type: "int", nullable: false),
                    Start = table.Column<TimeOnly>(type: "time", nullable: false),
                    End = table.Column<TimeOnly>(type: "time", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTimeSlots", x => x.Id);
                    table.CheckConstraint("CK_JobTimeSlots_StartBeforeEnd", "[Start] < [End]");
                    table.CheckConstraint("CK_JobTimeSlots_Weekday", "[Weekday] BETWEEN 1 AND 7");
                    table.ForeignKey(
                        name: "FK_JobTimeSlots_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobTimeSlots_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobTimeSlots_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobTimeSlots_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobSlotQualifications",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobSlotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QualificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSlotQualifications", x => new { x.TenantId, x.JobSlotId, x.QualificationId });
                    table.CheckConstraint("CK_JobSlotQualifications_Level", "[Level] IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_JobSlotQualifications_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobSlotQualifications_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobSlotQualifications_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobSlotQualifications_JobSlots_JobSlotId",
                        column: x => x.JobSlotId,
                        principalTable: "JobSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobSlotQualifications_Qualifications_QualificationId",
                        column: x => x.QualificationId,
                        principalTable: "Qualifications",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RequestRounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobSlotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstToAccept = table.Column<bool>(type: "bit", nullable: false),
                    RespondBy = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestRounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestRounds_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RequestRounds_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RequestRounds_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RequestRounds_JobSlots_JobSlotId",
                        column: x => x.JobSlotId,
                        principalTable: "JobSlots",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SlotAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobSlotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestRoundId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RespondBy = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastStaffCommentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCoordinatorCommentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StaffSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Modified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImpersonatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsInTransition = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlotAssignments_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SlotAssignments_AspNetUsers_ImpersonatedById",
                        column: x => x.ImpersonatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SlotAssignments_AspNetUsers_ModifiedById",
                        column: x => x.ModifiedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SlotAssignments_JobSlots_JobSlotId",
                        column: x => x.JobSlotId,
                        principalTable: "JobSlots",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SlotAssignments_RequestRounds_RequestRoundId",
                        column: x => x.RequestRoundId,
                        principalTable: "RequestRounds",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SlotAssignments_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationUserAttributeDefinitions_CreatedById",
                table: "ApplicationUserAttributeDefinitions",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationUserAttributeDefinitions_ImpersonatedById",
                table: "ApplicationUserAttributeDefinitions",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationUserAttributeDefinitions_ModifiedById",
                table: "ApplicationUserAttributeDefinitions",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationUserAttributeDefinitions_TenantId_Name",
                table: "ApplicationUserAttributeDefinitions",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationUserAttributes_EntityAttributeDefinitionId",
                table: "ApplicationUserAttributes",
                column: "EntityAttributeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_CreatedById",
                table: "AspNetUsers",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ImpersonatedById",
                table: "AspNetUsers",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ModifiedById",
                table: "AspNetUsers",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_TenantId",
                table: "AspNetUsers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClosedDays_CreatedById",
                table: "ClosedDays",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ClosedDays_ImpersonatedById",
                table: "ClosedDays",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ClosedDays_ModifiedById",
                table: "ClosedDays",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_ClosedDays_OrganizationUnitId",
                table: "ClosedDays",
                column: "OrganizationUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ClosedDays_TenantId",
                table: "ClosedDays",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ClosedDays_TenantId_FirstDay_LastDay",
                table: "ClosedDays",
                columns: new[] { "TenantId", "FirstDay", "LastDay" });

            migrationBuilder.CreateIndex(
                name: "IX_CommentContainers_CreatedById",
                table: "CommentContainers",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_CommentContainers_ImpersonatedById",
                table: "CommentContainers",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_CommentContainers_ModifiedById",
                table: "CommentContainers",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_CommentContainers_TenantId",
                table: "CommentContainers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CommentContainers_TenantId_EntityType_EntityId_Audience",
                table: "CommentContainers",
                columns: new[] { "TenantId", "EntityType", "EntityId", "Audience" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Comments_AuthorContactId",
                table: "Comments",
                column: "AuthorContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_AuthorSupplierId",
                table: "Comments",
                column: "AuthorSupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_CommentContainerId",
                table: "Comments",
                column: "CommentContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_CreatedById",
                table: "Comments",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_CrossTenantId",
                table: "Comments",
                column: "CrossTenantId",
                unique: true,
                filter: "[CrossTenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ImpersonatedById",
                table: "Comments",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ModifiedById",
                table: "Comments",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TenantId",
                table: "Comments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TenantId_CommentContainerId_Created",
                table: "Comments",
                columns: new[] { "TenantId", "CommentContainerId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TenantId_LinkedCrossTenantId",
                table: "Comments",
                columns: new[] { "TenantId", "LinkedCrossTenantId" },
                unique: true,
                filter: "[LinkedCrossTenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerContacts_CreatedById",
                table: "CustomerContacts",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerContacts_CustomerId_State",
                table: "CustomerContacts",
                columns: new[] { "CustomerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerContacts_ImpersonatedById",
                table: "CustomerContacts",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerContacts_LinkedUserId",
                table: "CustomerContacts",
                column: "LinkedUserId",
                unique: true,
                filter: "[LinkedUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerContacts_ModifiedById",
                table: "CustomerContacts",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerContacts_TenantId",
                table: "CustomerContacts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CreatedById",
                table: "Customers",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_DefaultUnitId",
                table: "Customers",
                column: "DefaultUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ImpersonatedById",
                table: "Customers",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_LinkedTenantId",
                table: "Customers",
                column: "LinkedTenantId",
                unique: true,
                filter: "[LinkedTenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ModifiedById",
                table: "Customers",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId",
                table: "Customers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_Name",
                table: "Customers",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_CorrelationId",
                table: "ErrorLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_Occurred",
                table: "ErrorLogs",
                column: "Occurred");

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationAudits_ImpersonatorId_Ended",
                table: "ImpersonationAudits",
                columns: new[] { "ImpersonatorId", "Ended" });

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationAudits_Started",
                table: "ImpersonationAudits",
                column: "Started");

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationAudits_TargetTenantId",
                table: "ImpersonationAudits",
                column: "TargetTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationAudits_TargetUserId",
                table: "ImpersonationAudits",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMessages_CreatedById",
                table: "IntegrationMessages",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMessages_ImpersonatedById",
                table: "IntegrationMessages",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMessages_ModifiedById",
                table: "IntegrationMessages",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMessages_Sequence",
                table: "IntegrationMessages",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMessages_State_NextAttempt",
                table: "IntegrationMessages",
                columns: new[] { "State", "NextAttempt" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMessages_TargetTenantId",
                table: "IntegrationMessages",
                column: "TargetTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMessages_TenantId",
                table: "IntegrationMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationMessages_TenantId_CorrelationId_Sequence",
                table: "IntegrationMessages",
                columns: new[] { "TenantId", "CorrelationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_CreatedById",
                table: "IntegrationReceipts",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_ImpersonatedById",
                table: "IntegrationReceipts",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_ModifiedById",
                table: "IntegrationReceipts",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_TenantId",
                table: "IntegrationReceipts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_TenantId_MessageId",
                table: "IntegrationReceipts",
                columns: new[] { "TenantId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobAttributeDefinitions_CreatedById",
                table: "JobAttributeDefinitions",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttributeDefinitions_ImpersonatedById",
                table: "JobAttributeDefinitions",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttributeDefinitions_ModifiedById",
                table: "JobAttributeDefinitions",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttributeDefinitions_TenantId_Name",
                table: "JobAttributeDefinitions",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobAttributes_EntityAttributeDefinitionId",
                table: "JobAttributes",
                column: "EntityAttributeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ContactId",
                table: "Jobs",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CoordinatorUserId",
                table: "Jobs",
                column: "CoordinatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedById",
                table: "Jobs",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CrossTenantId",
                table: "Jobs",
                column: "CrossTenantId",
                unique: true,
                filter: "[CrossTenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CustomerId",
                table: "Jobs",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ImpersonatedById",
                table: "Jobs",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_LinkedCrossTenantId",
                table: "Jobs",
                column: "LinkedCrossTenantId",
                unique: true,
                filter: "[LinkedCrossTenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ModifiedById",
                table: "Jobs",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OrganizationUnitId",
                table: "Jobs",
                column: "OrganizationUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId",
                table: "Jobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId_State_PeriodStart",
                table: "Jobs",
                columns: new[] { "TenantId", "State", "PeriodStart" });

            migrationBuilder.CreateIndex(
                name: "IX_JobSlotQualifications_CreatedById",
                table: "JobSlotQualifications",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlotQualifications_ImpersonatedById",
                table: "JobSlotQualifications",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlotQualifications_JobSlotId",
                table: "JobSlotQualifications",
                column: "JobSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlotQualifications_ModifiedById",
                table: "JobSlotQualifications",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlotQualifications_QualificationId",
                table: "JobSlotQualifications",
                column: "QualificationId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlotQualifications_TenantId",
                table: "JobSlotQualifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlots_CreatedById",
                table: "JobSlots",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlots_ImpersonatedById",
                table: "JobSlots",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlots_JobId_State",
                table: "JobSlots",
                columns: new[] { "JobId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_JobSlots_ModifiedById",
                table: "JobSlots",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlots_ServiceId",
                table: "JobSlots",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlots_TenantId",
                table: "JobSlots",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSlots_TenantId_ServiceId",
                table: "JobSlots",
                columns: new[] { "TenantId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobTimeSlots_CreatedById",
                table: "JobTimeSlots",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobTimeSlots_ImpersonatedById",
                table: "JobTimeSlots",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobTimeSlots_JobId_Weekday",
                table: "JobTimeSlots",
                columns: new[] { "JobId", "Weekday" });

            migrationBuilder.CreateIndex(
                name: "IX_JobTimeSlots_ModifiedById",
                table: "JobTimeSlots",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_JobTimeSlots_TenantId",
                table: "JobTimeSlots",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecipients_CreatedById",
                table: "MessageRecipients",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecipients_ImpersonatedById",
                table: "MessageRecipients",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecipients_ModifiedById",
                table: "MessageRecipients",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecipients_StoredMessageId_UserId",
                table: "MessageRecipients",
                columns: new[] { "StoredMessageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecipients_TenantId",
                table: "MessageRecipients",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecipients_TenantId_UserId_IsRead",
                table: "MessageRecipients",
                columns: new[] { "TenantId", "UserId", "IsRead" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecipients_UserId",
                table: "MessageRecipients",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OfferedServices_CreatedById",
                table: "OfferedServices",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OfferedServices_ImpersonatedById",
                table: "OfferedServices",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OfferedServices_ModifiedById",
                table: "OfferedServices",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_OfferedServices_SupplierId",
                table: "OfferedServices",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_OfferedServices_TenantId",
                table: "OfferedServices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OfferedServices_TenantId_SupplierId_LinkedCrossTenantId",
                table: "OfferedServices",
                columns: new[] { "TenantId", "SupplierId", "LinkedCrossTenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_CreatedById",
                table: "OrderLines",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_ImpersonatedById",
                table: "OrderLines",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_ModifiedById",
                table: "OrderLines",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_OfferedServiceId",
                table: "OrderLines",
                column: "OfferedServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_OrderId",
                table: "OrderLines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_TenantId",
                table: "OrderLines",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedById",
                table: "Orders",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CrossTenantId",
                table: "Orders",
                column: "CrossTenantId",
                unique: true,
                filter: "[CrossTenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ImpersonatedById",
                table: "Orders",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_LinkedCrossTenantId",
                table: "Orders",
                column: "LinkedCrossTenantId",
                unique: true,
                filter: "[LinkedCrossTenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ModifiedById",
                table: "Orders",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OrdererId",
                table: "Orders",
                column: "OrdererId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SupplierId",
                table: "Orders",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId",
                table: "Orders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_OrdererId_State",
                table: "Orders",
                columns: new[] { "TenantId", "OrdererId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderStaff_CreatedById",
                table: "OrderStaff",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderStaff_ImpersonatedById",
                table: "OrderStaff",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderStaff_ModifiedById",
                table: "OrderStaff",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrderStaff_OrderId_Position",
                table: "OrderStaff",
                columns: new[] { "OrderId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderStaff_TenantId",
                table: "OrderStaff",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitAttributeDefinitions_CreatedById",
                table: "OrganizationUnitAttributeDefinitions",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitAttributeDefinitions_ImpersonatedById",
                table: "OrganizationUnitAttributeDefinitions",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitAttributeDefinitions_ModifiedById",
                table: "OrganizationUnitAttributeDefinitions",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitAttributeDefinitions_TenantId_Name",
                table: "OrganizationUnitAttributeDefinitions",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitAttributes_EntityAttributeDefinitionId",
                table: "OrganizationUnitAttributes",
                column: "EntityAttributeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_CreatedById",
                table: "OrganizationUnits",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_ImpersonatedById",
                table: "OrganizationUnits",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_ModifiedById",
                table: "OrganizationUnits",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_ParentId",
                table: "OrganizationUnits",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_TenantId",
                table: "OrganizationUnits",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_TenantId_OneCompanyRoot",
                table: "OrganizationUnits",
                column: "TenantId",
                unique: true,
                filter: "[UnitType] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_TenantId_ParentId",
                table: "OrganizationUnits",
                columns: new[] { "TenantId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_TenantId_ParentId_Name",
                table: "OrganizationUnits",
                columns: new[] { "TenantId", "ParentId", "Name" },
                unique: true,
                filter: "[ParentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitUserRoles_CreatedById",
                table: "OrganizationUnitUserRoles",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitUserRoles_ImpersonatedById",
                table: "OrganizationUnitUserRoles",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitUserRoles_ModifiedById",
                table: "OrganizationUnitUserRoles",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitUserRoles_OrganizationUnitId",
                table: "OrganizationUnitUserRoles",
                column: "OrganizationUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitUserRoles_TenantId",
                table: "OrganizationUnitUserRoles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitUserRoles_TenantId_UserId_TenantRoleId_OrganizationUnitId",
                table: "OrganizationUnitUserRoles",
                columns: new[] { "TenantId", "UserId", "TenantRoleId", "OrganizationUnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitUserRoles_TenantRoleId",
                table: "OrganizationUnitUserRoles",
                column: "TenantRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnitUserRoles_UserId",
                table: "OrganizationUnitUserRoles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_CreatedById",
                table: "OutgoingMessages",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_ImpersonatedById",
                table: "OutgoingMessages",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_ModifiedById",
                table: "OutgoingMessages",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_Status_NextAttempt",
                table: "OutgoingMessages",
                columns: new[] { "Status", "NextAttempt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_TenantId",
                table: "OutgoingMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_TenantId_HistoryEntryId",
                table: "OutgoingMessages",
                columns: new[] { "TenantId", "HistoryEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_TenantId_Status",
                table: "OutgoingMessages",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Qualifications_CreatedById",
                table: "Qualifications",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Qualifications_ImpersonatedById",
                table: "Qualifications",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Qualifications_ModifiedById",
                table: "Qualifications",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Qualifications_TenantId",
                table: "Qualifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Qualifications_TenantId_Name",
                table: "Qualifications",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestRounds_CreatedById",
                table: "RequestRounds",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_RequestRounds_ImpersonatedById",
                table: "RequestRounds",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_RequestRounds_JobSlotId_State",
                table: "RequestRounds",
                columns: new[] { "JobSlotId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestRounds_ModifiedById",
                table: "RequestRounds",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_RequestRounds_RespondBy",
                table: "RequestRounds",
                columns: new[] { "TenantId", "State", "RespondBy" },
                filter: "[RespondBy] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RequestRounds_TenantId",
                table: "RequestRounds",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAbsences_CreatedById",
                table: "ResourceAbsences",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAbsences_ImpersonatedById",
                table: "ResourceAbsences",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAbsences_ModifiedById",
                table: "ResourceAbsences",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAbsences_ResourceId_State_FirstDay",
                table: "ResourceAbsences",
                columns: new[] { "ResourceId", "State", "FirstDay" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAbsences_TenantId",
                table: "ResourceAbsences",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePatternAssignments_CreatedById",
                table: "ResourcePatternAssignments",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePatternAssignments_ImpersonatedById",
                table: "ResourcePatternAssignments",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePatternAssignments_ModifiedById",
                table: "ResourcePatternAssignments",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePatternAssignments_ResourceId_State_EffectiveFrom",
                table: "ResourcePatternAssignments",
                columns: new[] { "ResourceId", "State", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePatternAssignments_TenantId",
                table: "ResourcePatternAssignments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourcePatternAssignments_WorkPatternId",
                table: "ResourcePatternAssignments",
                column: "WorkPatternId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceQualifications_CreatedById",
                table: "ResourceQualifications",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceQualifications_ImpersonatedById",
                table: "ResourceQualifications",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceQualifications_ModifiedById",
                table: "ResourceQualifications",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceQualifications_QualificationId",
                table: "ResourceQualifications",
                column: "QualificationId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceQualifications_ResourceId",
                table: "ResourceQualifications",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceQualifications_TenantId",
                table: "ResourceQualifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_CreatedById",
                table: "Resources",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ImpersonatedById",
                table: "Resources",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ModifiedById",
                table: "Resources",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_TenantId",
                table: "Resources",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_UserId",
                table: "Resources",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceUnitMemberships_CreatedById",
                table: "ResourceUnitMemberships",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceUnitMemberships_ImpersonatedById",
                table: "ResourceUnitMemberships",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceUnitMemberships_ModifiedById",
                table: "ResourceUnitMemberships",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceUnitMemberships_OrganizationUnitId",
                table: "ResourceUnitMemberships",
                column: "OrganizationUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceUnitMemberships_ResourceId",
                table: "ResourceUnitMemberships",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceUnitMemberships_TenantId",
                table: "ResourceUnitMemberships",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceQualifications_CreatedById",
                table: "ServiceQualifications",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceQualifications_ImpersonatedById",
                table: "ServiceQualifications",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceQualifications_ModifiedById",
                table: "ServiceQualifications",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceQualifications_QualificationId",
                table: "ServiceQualifications",
                column: "QualificationId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceQualifications_ServiceId",
                table: "ServiceQualifications",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceQualifications_TenantId",
                table: "ServiceQualifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Services_CreatedById",
                table: "Services",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Services_CrossTenantId",
                table: "Services",
                column: "CrossTenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Services_ImpersonatedById",
                table: "Services",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Services_ModifiedById",
                table: "Services",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Services_TenantId",
                table: "Services",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Services_TenantId_Name",
                table: "Services",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_CreatedById",
                table: "SlotAssignments",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_ImpersonatedById",
                table: "SlotAssignments",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_ModifiedById",
                table: "SlotAssignments",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_OneHolder",
                table: "SlotAssignments",
                column: "JobSlotId",
                unique: true,
                filter: "[State] = 4");

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_OneLivePerStaff",
                table: "SlotAssignments",
                columns: new[] { "JobSlotId", "ResourceId" },
                unique: true,
                filter: "[State] IN (1, 2, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_RequestRoundId",
                table: "SlotAssignments",
                column: "RequestRoundId");

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_ResourceId_State",
                table: "SlotAssignments",
                columns: new[] { "ResourceId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_RespondBy",
                table: "SlotAssignments",
                columns: new[] { "TenantId", "State", "RespondBy" },
                filter: "[RespondBy] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SlotAssignments_TenantId",
                table: "SlotAssignments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StateHistoryEntries_CreatedById",
                table: "StateHistoryEntries",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_StateHistoryEntries_ImpersonatedById",
                table: "StateHistoryEntries",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_StateHistoryEntries_ModifiedById",
                table: "StateHistoryEntries",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_StateHistoryEntries_TenantId",
                table: "StateHistoryEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StateHistoryEntries_TenantId_CorrelationId",
                table: "StateHistoryEntries",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_StateHistoryEntries_TenantId_EntityType_EntityId_Created",
                table: "StateHistoryEntries",
                columns: new[] { "TenantId", "EntityType", "EntityId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredMessages_CreatedById",
                table: "StoredMessages",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_StoredMessages_ImpersonatedById",
                table: "StoredMessages",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_StoredMessages_ModifiedById",
                table: "StoredMessages",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_StoredMessages_OutgoingMessageId",
                table: "StoredMessages",
                column: "OutgoingMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoredMessages_TenantId",
                table: "StoredMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_CreatedById",
                table: "Suppliers",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_ImpersonatedById",
                table: "Suppliers",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_ModifiedById",
                table: "Suppliers",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId",
                table: "Suppliers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_LinkedTenantId",
                table: "Suppliers",
                columns: new[] { "TenantId", "LinkedTenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantRolePermissions_CreatedById",
                table: "TenantRolePermissions",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRolePermissions_ImpersonatedById",
                table: "TenantRolePermissions",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRolePermissions_ModifiedById",
                table: "TenantRolePermissions",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRolePermissions_TenantId",
                table: "TenantRolePermissions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRolePermissions_TenantId_TenantRoleId_Permission",
                table: "TenantRolePermissions",
                columns: new[] { "TenantId", "TenantRoleId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantRolePermissions_TenantRoleId",
                table: "TenantRolePermissions",
                column: "TenantRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRoles_CreatedById",
                table: "TenantRoles",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRoles_ImpersonatedById",
                table: "TenantRoles",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRoles_ModifiedById",
                table: "TenantRoles",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRoles_TenantId",
                table: "TenantRoles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantRoles_TenantId_SystemOwner",
                table: "TenantRoles",
                column: "TenantId",
                unique: true,
                filter: "[IsSystemOwner] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_IsRootTenant",
                table: "Tenants",
                column: "IsRootTenant",
                unique: true,
                filter: "[IsRootTenant] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Name",
                table: "Tenants",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_ParentTenantId",
                table: "Tenants",
                column: "ParentTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_CreatedById",
                table: "TenantSettings",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_ImpersonatedById",
                table: "TenantSettings",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_ModifiedById",
                table: "TenantSettings",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId",
                table: "TenantSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_Key",
                table: "TenantSettings",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatterns_CreatedById",
                table: "WorkPatterns",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatterns_ImpersonatedById",
                table: "WorkPatterns",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatterns_ModifiedById",
                table: "WorkPatterns",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatterns_TenantId",
                table: "WorkPatterns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatternSegments_CreatedById",
                table: "WorkPatternSegments",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatternSegments_ImpersonatedById",
                table: "WorkPatternSegments",
                column: "ImpersonatedById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatternSegments_ModifiedById",
                table: "WorkPatternSegments",
                column: "ModifiedById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatternSegments_TenantId",
                table: "WorkPatternSegments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPatternSegments_WorkPatternId_DayIndex",
                table: "WorkPatternSegments",
                columns: new[] { "WorkPatternId", "DayIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationUserAttributes");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "ClosedDays");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "ErrorLogs");

            migrationBuilder.DropTable(
                name: "ImpersonationAudits");

            migrationBuilder.DropTable(
                name: "IntegrationMessages");

            migrationBuilder.DropTable(
                name: "IntegrationReceipts");

            migrationBuilder.DropTable(
                name: "JobAttributes");

            migrationBuilder.DropTable(
                name: "JobSlotQualifications");

            migrationBuilder.DropTable(
                name: "JobTimeSlots");

            migrationBuilder.DropTable(
                name: "MessageRecipients");

            migrationBuilder.DropTable(
                name: "OrderLines");

            migrationBuilder.DropTable(
                name: "OrderStaff");

            migrationBuilder.DropTable(
                name: "OrganizationUnitAttributes");

            migrationBuilder.DropTable(
                name: "OrganizationUnitUserRoles");

            migrationBuilder.DropTable(
                name: "ResourceAbsences");

            migrationBuilder.DropTable(
                name: "ResourcePatternAssignments");

            migrationBuilder.DropTable(
                name: "ResourceQualifications");

            migrationBuilder.DropTable(
                name: "ResourceUnitMemberships");

            migrationBuilder.DropTable(
                name: "ServiceQualifications");

            migrationBuilder.DropTable(
                name: "SlotAssignments");

            migrationBuilder.DropTable(
                name: "StateHistoryEntries");

            migrationBuilder.DropTable(
                name: "TenantRolePermissions");

            migrationBuilder.DropTable(
                name: "TenantSettings");

            migrationBuilder.DropTable(
                name: "WorkPatternSegments");

            migrationBuilder.DropTable(
                name: "ApplicationUserAttributeDefinitions");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "CommentContainers");

            migrationBuilder.DropTable(
                name: "JobAttributeDefinitions");

            migrationBuilder.DropTable(
                name: "StoredMessages");

            migrationBuilder.DropTable(
                name: "OfferedServices");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "OrganizationUnitAttributeDefinitions");

            migrationBuilder.DropTable(
                name: "Qualifications");

            migrationBuilder.DropTable(
                name: "RequestRounds");

            migrationBuilder.DropTable(
                name: "Resources");

            migrationBuilder.DropTable(
                name: "TenantRoles");

            migrationBuilder.DropTable(
                name: "WorkPatterns");

            migrationBuilder.DropTable(
                name: "OutgoingMessages");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropTable(
                name: "JobSlots");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Services");

            migrationBuilder.DropTable(
                name: "CustomerContacts");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "OrganizationUnits");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
