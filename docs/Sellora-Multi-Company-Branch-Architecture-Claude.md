# Sellora Billing

# Multi-Company, Multi-Branch, Governance, Authorization and Inventory Architecture

## Claude Code Incremental Implementation Specification

  -----------------------------------------------------------------------
  Document property                   Value
  ----------------------------------- -----------------------------------
  Product                             Sellora Billing

  Document type                       Architecture and implementation
                                      specification

  Version                             1.0

  Status                              Approved implementation baseline

  Target stack                        .NET 10, ASP.NET Core Web API, EF
                                      Core, PostgreSQL, Angular

  Repository                          Sellora Billing

  Primary objective                   Safely evolve the existing
                                      single-office implementation into a
                                      multi-company, multi-branch ERP

  Execution model                     Small, independently verifiable
                                      phases; explicit approval between
                                      phases
  -----------------------------------------------------------------------

------------------------------------------------------------------------

# 1. Purpose

This document defines the target architecture and controlled
implementation plan for evolving Sellora Billing from its current
single-office business setup into a production-ready organizational,
authorization and inventory-location architecture.

The target must support:

-   Multiple companies.
-   Multiple branches per company.
-   Company-level and branch-level warehouses.
-   Branch stock locations.
-   Vehicles and van inventory.
-   Unified stock-location and stock-movement traceability.
-   Company-level users.
-   Branch-level users.
-   Multiple branch allocations for a user.
-   Historical branch allocation records.
-   Platform-level administration, separate from customer-company
    governance.
-   Company owners and board members with individually assigned
    governance permissions.
-   Company-scoped and branch-scoped roles.
-   Strict company and branch authorization boundaries.
-   Stock transfers, transit, partial receipt and discrepancy handling.
-   Integration with existing purchase, sales, returns, batch and
    barcode workflows.
-   Safe migration of existing Sellora data without losing historical
    transactions.

This is an incremental refactoring specification. Claude Code must
inspect the existing repository and adapt the implementation to the
actual codebase. Do not rebuild Sellora from scratch.

# 2. Explicit scope and exclusions

## 2.1 Included

The implementation includes:

1.  Company and branch organization.
2.  Company and branch warehouse ownership.
3.  Unified stock locations.
4.  Vehicle master data and vehicle stock locations.
5.  User-to-company membership.
6.  User-to-branch allocations, including multiple concurrent
    allocations.
7.  Primary branch designation.
8.  Historical branch assignment and access-change records.
9.  Company governance membership for owners and board members.
10. Individually assigned governance permissions.
11. Delegated company administrators.
12. Platform Super Admin separation.
13. Company-scoped and branch-scoped RBAC.
14. User role assignments and permission evaluation.
15. Inventory balances by stock location.
16. Stock movement ledger and transfer lifecycle.
17. Purchase, sales, return and van-sales location integration.
18. Backend API, service, database and Angular changes.
19. Tests, migration, deployment and operational documentation.

## 2.2 Explicitly excluded: employee and HR management

Do not implement an employee/HR module as part of this work.

Do not create:

-   Employee master/profile module.
-   Recruitment or onboarding workflows.
-   Attendance.
-   Payroll.
-   Employment contracts.
-   Leave management.
-   Performance management.
-   HR organizational records.
-   Employee transfer or employee employment-history entities.

This specification manages **application users and their company/branch
access**, not employment records.

A user can be allocated to one or more branches through user access
assignment records. These records must not depend on an Employee entity.

Where the business uses the term "transfer" in this document, it means a
change to a user's company/branch access assignment unless explicitly
stated otherwise. It is not an HR employment transfer workflow.

# 3. Mandatory repository discovery and architecture preservation

Before modifying code, inspect the current repository and produce a
baseline report.

Expected project areas include, but are not limited to:

-   `backend/Sellora.slnx`
-   `backend/src/Sellora.Api`
-   `backend/src/Sellora.Domain`
-   `backend/src/Sellora.Infrastructure`
-   `backend/tests/Sellora.Tests`
-   `frontend`
-   Existing EF Core DbContext and configurations.
-   Existing migrations and database naming conventions.
-   Authentication and JWT implementation.
-   Authorization policies and permission handling.
-   Existing user and role entities.
-   Existing company, branch, warehouse or location concepts, if any.
-   Product, unit, batch and barcode models.
-   Purchase, purchase return and receiving workflows.
-   Sales, sales return and billing workflows.
-   Inventory balance, adjustment and movement logic.
-   Financial transactions and audit implementation.
-   Angular routing, guards, interceptors, services and shared UI
    components.
-   Existing API versioning, DTO, validation and error-handling
    conventions.
-   Existing CI/CD and deployment configuration.

## 3.1 Preserve the existing architecture

Backend:

-   Use the existing .NET 10 solution and project structure.
-   Keep controllers thin.
-   Put business rules and orchestration in application/service classes
    consistent with the existing architecture.
-   Keep persistence implementation in Infrastructure.
-   Keep domain concepts and contracts in their established projects.
-   Do not inject DbContext into controllers for business operations.
-   Do not introduce a Repository layer if the existing architecture
    intentionally does not use one.
-   Use asynchronous EF Core operations and cancellation tokens.
-   Use DTOs rather than exposing EF entities from APIs.
-   Follow existing validation, error handling, logging and
    authorization conventions.

Frontend:

-   Follow the existing Angular version and project structure.
-   Reuse existing shared UI components and design conventions.
-   Keep API communication in services.
-   Keep authorization-aware navigation and UI logic reusable.
-   Do not treat frontend guards or hidden buttons as security
    boundaries.

General:

-   Do not replace working modules without evidence that a change is
    required.
-   Do not rename or move large groups of files without a clear benefit.
-   Do not add packages without checking whether the existing stack
    already provides the required capability.
-   Do not modify unrelated modules.
-   Record deviations from this specification in an architecture
    decision record (ADR).

# 4. Target organizational model

The organizational model is:

``` text
ERP Platform
|
+-- Company A
|   |
|   +-- Company Governance Members
|   +-- Company Users
|   +-- Company-scoped Roles
|   +-- Company-level Warehouses
|   +-- Vehicles
|   |
|   +-- Branch A1
|   |   +-- Branch users/access assignments
|   |   +-- Branch-scoped Roles
|   |   +-- Branch warehouses
|   |   +-- Branch stock location
|   |
|   +-- Branch A2
|   |   +-- Branch users/access assignments
|   |   +-- Branch-scoped Roles
|   |   +-- Branch warehouses
|   |   +-- Branch stock location
|   |
|   +-- Additional branches...
|
+-- Company B
    +-- Independent governance, users, roles, branches and inventory
```

## 4.1 Company rules

-   A company is the tenant and ownership boundary for business data.
-   Every branch belongs to exactly one company.
-   Every warehouse belongs to exactly one company.
-   Every vehicle belongs to exactly one company.
-   A user-company membership is explicit.
-   Company data must never be exposed to a different company.
-   Company-level authority is limited to that company.
-   A company owner is not automatically a platform administrator.

## 4.2 Branch rules

-   A branch belongs to exactly one company.
-   A branch may have its own stock location.
-   A branch may have multiple warehouses.
-   A branch can have users allocated to it.
-   A user may have multiple active branch allocations within the same
    company.
-   Branch allocation does not automatically grant branch administration
    rights.
-   Branch administrators cannot manage another branch's user
    allocations or roles unless a separately authorized company-level
    delegation explicitly permits the operation. The default policy is
    deny.

# 5. Platform administration and company governance

These are separate security domains and must not be combined into one
"Super Admin" role.

## 5.1 Platform Super Admin

Platform Super Admin operates the Sellora platform, not a customer's
company.

Platform-level capabilities may include:

-   Platform configuration.
-   Tenant/company lifecycle administration.
-   Subscription and platform operations.
-   Platform security administration.
-   Authorized support operations.

Platform Super Admin must not automatically be:

-   A company owner.
-   A company board member.
-   A company administrator.
-   A member of every company's normal operational roles.

Any exceptional platform support access to customer data must be
explicitly authorized, limited, time-bound where practical and audited.
Do not create an unrestricted hidden bypass in ordinary business APIs.

## 5.2 Company governance members

A company may have:

-   One owner.
-   Multiple owners.
-   Multiple board members.
-   Other governance members, if required by the product.

Governance membership belongs to one company and one user.

**Approved governance decision: individual permissions.**

Each governance member receives explicitly assigned governance
permissions. Do not assume all owners or board members have identical
software permissions merely because of their title.

Examples:

-   Owner A: full company governance permissions.
-   Board Member B: view governance, manage administrators and approve
    selected sensitive operations.
-   Board Member C: view governance and approve high-risk changes.
-   Delegated Governance Administrator: manage specified users or roles
    without ownership-management authority.

The implementation must distinguish:

1.  Legal/business title or membership type (Owner, Board Member, etc.).
2.  Explicit governance permissions.
3.  Ordinary operational roles.

A title must not silently grant permissions. Effective authority must
come from explicit, auditable permission assignments and any configured
approval rules.

## 5.3 Governance permission catalogue

Use stable permission keys. Initial candidates:

``` text
Company.Governance.View
Company.Governance.ManageMembers
Company.Governance.ManageOwners
Company.Governance.AssignPermissions
Company.Governance.ApproveSensitiveActions

Company.Users.View
Company.Users.Manage
Company.Users.ManageBranchAccess
Company.Users.TransferAccess
Company.Users.Suspend

Company.Branches.View
Company.Branches.Manage

Company.Warehouses.View
Company.Warehouses.Manage

Company.Vehicles.View
Company.Vehicles.Manage

Company.Roles.View
Company.Roles.Manage
Company.Permissions.Assign

Company.Security.Manage
Company.Settings.Manage
```

Claude must inspect existing permissions before adding these. Reuse
existing permission naming conventions where possible. Do not create
duplicate or near-duplicate permission keys.

## 5.4 Sensitive governance operations

Sensitive operations include, at minimum:

-   Adding or removing governance members.
-   Changing owner membership.
-   Assigning or removing governance permissions.
-   Granting company-wide administrative access.
-   Granting role/permission administration.
-   Suspending a highly privileged account.
-   Changing security-critical company settings.

The first implementation must support individual authorization and
complete audit history. Multi-person approval workflows should be
introduced only if the existing product requirements or an approved ADR
require them. Do not invent a complex board-voting system in this phase.

The system must prevent a user from granting themselves additional
governance permissions unless an independently authorized process
permits it.

# 6. User identity, company membership and branch allocations

Keep authentication identity separate from organizational access.

Recommended conceptual relationships:

``` text
User
 |
 +-- UserCompanyMembership
       |
       +-- UserBranchAssignment (zero or more)
       |
       +-- UserRoleAssignment (zero or more)
```

Use the existing user identity model if one already exists. Do not
duplicate user accounts solely to represent branch access.

## 6.1 Company membership

A user must have an explicit active membership in a company before
accessing that company's business data.

A company membership may be company-wide or restricted to assigned
branches. Model this explicitly. Do not infer company-wide access from
the absence of branch rows.

Recommended conceptual fields:

-   Id
-   UserId
-   CompanyId
-   MembershipStatus
-   AccessScope (CompanyWide or AssignedBranches)
-   CreatedAt
-   CreatedBy
-   UpdatedAt
-   UpdatedBy

Adapt names and types to existing conventions.

## 6.2 Branch allocations

A user may have multiple active branch allocations in the same company.

Examples:

  User                Company     Active branch allocations   Primary branch
  ------------------- ----------- --------------------------- ----------------
  User A              Company 1   Branch 1                    Branch 1
  User B              Company 1   Branch 1, Branch 2          Branch 1
  User C              Company 1   Branch 2, Branch 3          Branch 2
  Company-wide user   Company 1   No branch restriction       None

A company-wide user does not need individual branch allocations for
access, but their company-wide authority must be explicitly granted
through membership scope and permissions.

## 6.3 Historical allocation decision

**Approved decision: retain historical branch allocation records.**

Branch assignments must support effective dates and historical state.

Recommended conceptual fields:

-   Id
-   UserCompanyMembershipId
-   CompanyId
-   BranchId
-   IsPrimary
-   StartAt
-   EndAt (nullable)
-   Status
-   AssignmentReason (optional)
-   CreatedAt
-   CreatedBy
-   EndedAt (nullable)
-   EndedBy (nullable)

Use UTC timestamps for audit and effective instants. If the product
models business-effective dates separately, document the distinction and
use the company's configured timezone for display and date
interpretation.

Rules:

-   A user may have multiple active branch assignments.
-   A user may have at most one active primary branch within a company.
-   Every assignment's branch must belong to the same company as the
    membership.
-   Ending an assignment must preserve its historical row.
-   Do not physically delete assignment history during normal
    operations.
-   Prevent overlapping duplicate active assignments for the same
    user/company/branch.
-   Enforce at most one active primary branch per user/company.
-   A user with no active branch assignment must not access
    branch-scoped data unless the company membership explicitly grants
    company-wide access.
-   Branch allocation and role assignment are different concepts.
-   Removing a branch allocation must cause effective branch access to
    be recalculated or denied.
-   Historical records must remain queryable for audit.

PostgreSQL indexes and constraints should enforce invariants where
practical. Use partial unique indexes for active assignments and primary
branch constraints, taking care with nullable fields and status
semantics.

## 6.4 Who can manage allocations?

Default authorization:

  --------------------------------------------------------------------------
  Operation      Platform Super Company        Delegated      Branch admin
                 Admin          governance     company admin  
                                member with    with           
                                permission     permission     
  -------------- -------------- -------------- -------------- --------------
  Allocate user  Exceptional    Yes            Yes, if        No
  to branch      support only                  delegated      

  Allocate user  Exceptional    Yes            Yes, if        No
  to multiple    support only                  delegated      
  branches                                                    

  Remove branch  Exceptional    Yes            Yes, if        No
  allocation     support only                  delegated      

  Change primary Exceptional    Yes            Yes, if        No
  branch         support only                  delegated      

  Change company Exceptional    Yes            Only if        No
  membership     support only                  delegated      
  scope                                                       

  Modify own     No implicit    Only through   No implicit    No
  branch         right          authorized     right          
  allocations                   governance                    
                                process                       
  --------------------------------------------------------------------------

Do not grant these permissions to every company administrator by
default. Permissions must be explicitly assigned.

## 6.5 Access reassignment workflow

Support access reassignment as a controlled operation:

1.  Authorize the acting user.
2.  Verify the target user has an active membership in the company.
3.  Verify source and destination branches belong to that company.
4.  Verify the actor has permission to manage the target user's access.
5.  Record the reason when required by the workflow.
6.  End the old assignment or assignments as appropriate.
7.  Create the new assignment(s).
8.  Enforce primary-branch rules.
9.  Recalculate effective access and invalidate relevant cached
    authorization data.
10. Write an immutable audit event.
11. Commit the operation transactionally.

A multi-branch temporary allocation can leave the primary branch
unchanged while adding an additional assignment. A permanent access
reassignment can close the previous assignment and create the new
primary assignment.

Do not create EmployeeTransferHistory or any HR entity.

# 7. Scoped role-based access control (RBAC)

Implement two operational role scopes:

1.  Company.
2.  Branch.

Keep governance permissions separate from ordinary operational RBAC.

## 7.1 Company-scoped roles

A company role belongs to one company and applies within that company
according to its permissions.

Examples:

-   Company Admin.
-   Finance Manager.
-   Procurement Manager.
-   Operations Manager.
-   Inventory Controller.

A company-scoped role must never grant access to another company.

Company-wide permission scope must be explicit. Do not treat a role name
such as "Admin" as authority.

## 7.2 Branch-scoped roles

A branch role belongs to exactly one branch and one company.

Examples:

Branch 1: - Branch Admin. - Branch Sales. - Branch Inventory.

Branch 2: - Branch Admin. - Branch Sales. - Branch Inventory.

Identical names in different branches are separate role records with
independent permission assignments.

A Branch 1 administrator must not read, create, update, delete, assign
or modify Branch 2 roles.

A branch administrator can manage roles only when explicitly granted the
relevant branch-scoped role-management permissions.

## 7.3 Recommended role data

Adapt the existing role schema instead of blindly creating a duplicate.

Conceptual fields:

-   Id
-   CompanyId
-   ScopeType (Company or Branch)
-   BranchId (nullable for Company scope; required for Branch scope)
-   Name
-   Description
-   IsSystemRole
-   IsActive
-   CreatedAt
-   CreatedBy
-   UpdatedAt
-   UpdatedBy

Constraints:

-   Company-scoped roles must have BranchId null.
-   Branch-scoped roles must have a non-null BranchId.
-   BranchId must belong to CompanyId.
-   Role names must be unique within their scope, using case-insensitive
    uniqueness if that matches product conventions.
-   Protected system roles must not be modified or deleted through
    ordinary role-management operations.

PostgreSQL partial unique indexes are suitable for separate company and
branch uniqueness rules.

## 7.4 Role permissions

Use a central permission catalogue and role-permission assignments.

Conceptual tables:

-   Permissions
-   Roles
-   RolePermissions
-   UserRoleAssignments

A permission should have a stable key, description, module/category and
active state as appropriate.

RolePermissions must reference valid permissions and roles.

UserRoleAssignments must include enough scope information to prove that
the user, company, role and optional branch are compatible.

Use composite foreign keys or equivalent database constraints where
practical to prevent cross-company or cross-branch relationships.

## 7.5 Effective permission evaluation

Do not implicitly merge company and branch permissions without a
documented rule.

The implementation must define and test:

-   Company-scoped role evaluation.
-   Branch-scoped role evaluation.
-   Company-wide membership access.
-   Assigned-branch membership access.
-   Multiple branch allocations.
-   Multiple roles within a scope.
-   Revoked or inactive memberships.
-   Inactive roles and permissions.
-   Governance permissions versus operational permissions.

Recommended rule:

A company-scoped permission can authorize operations across that company
only when the user's active company membership, assigned company role
and permission scope explicitly allow it.

A branch-scoped permission applies only to its exact branch and requires
an active user branch assignment (unless a separately approved
company-wide access rule explicitly covers the operation).

Do not let a branch role grant company-wide access.

Do not let a branch allocation itself grant permissions.

Do not let governance membership silently grant every operational
permission.

## 7.6 Prevent privilege escalation

A user must not:

-   Assign permissions they are not authorized to delegate.
-   Modify roles outside their scope.
-   Grant themselves a role with greater authority.
-   Grant themselves governance permissions.
-   Change a role's scope to escape branch restrictions.
-   Change CompanyId or BranchId to access another tenant.
-   Assign a user to a branch outside the actor's authorized company.
-   Modify protected system roles.
-   Assign a role to a user whose membership or branch allocation is
    invalid.

Role management, permission delegation, user-role assignment and
governance membership management require separate authorization checks.

# 8. Inventory location architecture

Introduce or refactor a unified StockLocation concept.

Every internal location that can hold sellable or otherwise tracked
inventory must have a StockLocation record.

Initial location types:

-   Branch stock location.
-   Company warehouse.
-   Branch warehouse.
-   Vehicle.
-   Transit.

External supplier and customer parties should remain represented by
their business entities and transaction references unless the existing
inventory design requires external stock locations. Do not create
unnecessary external locations merely to make every transaction look
like an internal transfer.

Conceptual StockLocation fields:

-   Id
-   CompanyId
-   BranchId (nullable)
-   LocationType
-   Name
-   Code
-   IsActive
-   CreatedAt
-   CreatedBy
-   UpdatedAt
-   UpdatedBy

Rules:

-   Every stock location belongs to one company.
-   BranchId is nullable for company-level locations.
-   If BranchId is populated, the branch must belong to CompanyId.
-   A branch stock location must reference its branch.
-   A company warehouse has no BranchId.
-   A branch warehouse references its owning branch.
-   A vehicle location references the vehicle through the
    vehicle/location relationship.
-   A transit location must be scoped so that goods in transit remain
    attributable to the owning company and transfer.
-   Location deactivation must not erase or invalidate historical
    movements.
-   Locations with stock must not be deleted.
-   Do not permit movement between different companies.

The implementation may use a dedicated location subtype/reference table
if that fits the existing domain better. Avoid fragile polymorphic
foreign keys without referential integrity.

# 9. Warehouse and vehicle management

## 9.1 Warehouses

Support both:

-   Company-level warehouse: CompanyId populated, BranchId null.
-   Branch-level warehouse: CompanyId populated, BranchId populated.

A branch may have multiple warehouses. A company may have multiple
central/distribution warehouses.

All warehouses must have corresponding stock locations.

## 9.2 Vehicles

Vehicles are inventory-holding operational assets, not just descriptive
records.

Conceptual fields:

-   Id
-   CompanyId
-   VehicleNumber
-   VehicleName
-   VehicleType
-   AssignedBranchId (nullable)
-   StockLocationId
-   IsActive
-   CreatedAt
-   CreatedBy
-   UpdatedAt
-   UpdatedBy

Rules:

-   Every vehicle belongs to one company.
-   A vehicle may be centrally operated or assigned to a branch.
-   AssignedBranchId must belong to the same company.
-   Vehicle stock is held at its StockLocation.
-   Vehicle reassignment must not silently move or re-own inventory.
-   Before reassignment, existing stock must be transferred, returned or
    explicitly reconciled.
-   Vehicle deactivation must be blocked while unresolved stock or
    transfers remain, unless an approved controlled process handles
    them.

Support company-owned vehicles assigned to branches and vehicles
operated centrally. Do not create employee/driver or HR entities as part
of this scope.

# 10. Inventory data model and ledger

Inspect and reuse the existing inventory model before creating new
entities.

The target architecture must support:

-   Inventory balance by company and stock location.
-   Product and unit.
-   Batch/lot where applicable.
-   Barcode traceability where applicable.
-   Stock movement references.
-   Source and destination location.
-   Transfers and transit.
-   Purchase receiving.
-   Sales issue.
-   Purchase returns.
-   Sales returns.
-   Adjustments.
-   Opening balances.
-   Vehicle loading and van sales.

Conceptual entities:

-   StockLocations
-   InventoryBalances
-   StockTransfers
-   StockTransferItems
-   StockMovements
-   StockMovementItems

## 10.1 Inventory balance key

The logical balance key is:

`CompanyId + StockLocationId + ProductId + BatchId + UnitId`

Use appropriate PostgreSQL uniqueness semantics for nullable BatchId.
PostgreSQL versions that support `NULLS NOT DISTINCT` may use it;
otherwise use a safe equivalent. Confirm the actual PostgreSQL server
version before choosing the syntax.

Balance records are a performance projection/current-state
representation. The stock movement ledger is the auditable source of
inventory changes.

## 10.2 Stock movement

A posted movement must preserve:

-   CompanyId.
-   ProductId.
-   BatchId where applicable.
-   Source location where applicable.
-   Destination location where applicable.
-   Quantity.
-   UnitId.
-   Movement type.
-   Reference document type and ID.
-   Posting/effective timestamp.
-   Created by and posted by.
-   Reversal/correction relationship where applicable.

Do not allow direct editing or deletion of posted movements.

Correct posted mistakes through reversal and replacement/adjustment
transactions with complete references and audit records.

## 10.3 Consistency and concurrency

-   Post related stock ledger entries and balance updates in a database
    transaction.
-   Use concurrency control to prevent overselling or double dispatch.
-   Validate available quantity at the authoritative stock location.
-   Use idempotency or duplicate-post protection for retryable posting
    operations.
-   Define a consistent transaction and locking strategy.
-   Avoid negative stock unless an explicit existing business policy
    allows it.
-   Ensure balance projections can be reconciled against the ledger.
-   Do not update inventory balances from controllers.
-   Centralize stock posting in the existing inventory service/domain
    workflow.

# 11. Stock movement scenarios

The unified stock movement service must support applicable scenarios
including:

## Supplier and purchasing

-   Supplier to company warehouse.
-   Supplier to branch.
-   Supplier to branch warehouse.

These are purchase receipt transactions, not ordinary internal
transfers.

## Internal company movements

-   Company warehouse to branch.
-   Company warehouse to branch warehouse.
-   Company warehouse to another company warehouse.
-   Branch stock location to branch warehouse.
-   Branch warehouse to branch stock location.
-   Branch warehouse to another warehouse in the same company.
-   Branch 1 to Branch 2.
-   Branch warehouse to vehicle.
-   Company warehouse to vehicle.
-   Vehicle to branch.
-   Vehicle 1 to Vehicle 2.

## Sales and returns

-   Vehicle to customer through van sales.
-   Branch/warehouse to customer through normal sales.
-   Customer return to an authorized branch/warehouse location.
-   Branch/warehouse to supplier through purchase return.

External party transactions must preserve their purchase/sales/return
document relationship and must not be misrepresented as ordinary
internal transfers.

# 12. Stock transfer workflow

Implement a consistent transfer lifecycle. Adapt status names to
existing conventions.

Suggested states:

-   Draft
-   Requested
-   Approved
-   Rejected
-   Dispatched
-   PartiallyReceived
-   Received
-   Completed
-   Cancelled

The exact state machine must prevent invalid transitions.

## 12.1 Workflow

1.  Create transfer request.
2.  Validate company, source and destination.
3.  Validate product, batch, unit and requested quantities.
4.  Apply approval policy if required.
5.  Dispatch actual quantities.
6.  Move dispatched stock from source to transit.
7.  Record goods-in-transit traceability.
8.  Receive actual quantities at destination.
9.  Record shortage, excess or damage separately.
10. Move accepted quantity from transit to destination.
11. Resolve remaining discrepancy or transit quantity.
12. Complete the transfer.
13. Record all actors, timestamps and audit events.

## 12.2 Quantity rules

Keep requested, dispatched, received, accepted, damaged and outstanding
quantities distinct where the workflow requires them.

Do not assume dispatched quantity equals received quantity.

Partial receipts must be supported.

Do not deduct source stock twice.

On dispatch: - Decrease source available stock by the dispatched
quantity. - Increase transit stock by the same quantity.

On receipt: - Decrease transit stock by the quantity resolved from
transit. - Increase destination stock by the accepted quantity. - Record
damaged/short/excess quantities through explicit discrepancy handling.

The sum of stock quantities and discrepancy resolutions must reconcile
with the dispatched quantity.

A transfer cannot be marked complete while unresolved transit quantity
remains, unless an authorized discrepancy-resolution operation
explicitly closes it with a traceable adjustment.

## 12.3 Approval

Approval requirements may depend on:

-   Source location.
-   Destination location.
-   Value or quantity thresholds.
-   Company policy.
-   Actor's permissions.

Do not hardcode a single approver role. Use permission and policy
checks.

# 13. Purchase, sales, returns and van integration

All existing workflows must be inspected before changes.

## 13.1 Purchase

Purchase receipt must identify the destination StockLocation.

Support authorized receipts into:

-   Company warehouse.
-   Branch stock location.
-   Branch warehouse.

Preserve supplier, purchase invoice, purchase items, batches, costs, tax
and financial relationships.

## 13.2 Sales

Sales must consume stock from the location selected by the operational
context and validated by the backend.

A branch user must not sell stock from another branch merely by changing
a request location ID.

Van sales must consume stock from the vehicle's stock location.

## 13.3 Sales return

A sales return must specify or resolve an authorized return destination.

Returned goods must be classified according to existing product/quality
rules, such as saleable, damaged or quarantine where supported. Do not
automatically add every returned item to available saleable stock
without applying the existing business rules.

Preserve the original sale and batch traceability where possible.

## 13.4 Purchase return

A purchase return must reduce stock from the actual location holding the
returned goods. If the goods were transferred after purchase, the return
workflow must identify the current source location and preserve the
original purchase reference.

## 13.5 Van operations

Support:

-   Loading stock from an authorized warehouse/branch location to a
    vehicle.
-   Van sales.
-   Van stock inquiry.
-   Return of unsold stock.
-   Damage and shortage recording.
-   Vehicle-to-vehicle transfer when authorized.
-   Vehicle reassignment only after stock is resolved.

All these operations use the same inventory posting service.

# 14. API and backend architecture

Follow the existing service-layer architecture.

Potential API feature areas:

-   Companies
-   Branches
-   Warehouses
-   Vehicles
-   StockLocations
-   Users
-   UserBranchAssignments
-   Governance
-   Roles
-   Permissions
-   Inventory
-   StockTransfers

These are feature areas, not an instruction to create a controller for
every table.

Controllers must:

-   Authenticate.
-   Apply coarse-grained endpoint policies.
-   Validate request shape.
-   Delegate business operations to services.
-   Return the established API response format.
-   Pass CancellationToken.

Services must perform authoritative checks for:

-   Company ownership.
-   Branch ownership.
-   User membership and branch allocation.
-   Role scope.
-   Governance permissions.
-   Source/destination location compatibility.
-   Stock availability.
-   Transfer state transitions.
-   Duplicate/replayed posting requests.

Never trust a CompanyId, BranchId, StockLocationId, RoleId or UserId
supplied by a client without server-side validation.

Prefer resource-based authorization where access depends on the
requested company, branch, role, user or location.

Do not rely only on a static `[Authorize]` policy to validate resource
ownership.

## 14.1 Data access

-   Use the existing DbContext and EF Core configuration patterns.
-   Use `AsNoTracking` for read-only queries where appropriate.
-   Project to DTOs.
-   Avoid N+1 queries.
-   Apply pagination to large lists.
-   Ensure tenant and branch predicates are applied in the database
    query.
-   Use indexes aligned with actual query patterns.
-   Use transactions for multi-record business operations.
-   Keep business validation out of controllers.

## 14.2 API contracts

Use explicit request/response DTOs.

Do not expose persistence entities.

Validate IDs and relationships on the server.

Use consistent error responses for: - Not found. - Unauthorized. -
Forbidden. - Validation failure. - Conflict/concurrency failure. -
Invalid workflow state.

Do not disclose another company's record existence through error
differences where that would create an information leak.

# 15. Angular implementation

Inspect the existing Angular feature organization and reuse established
patterns.

Potential feature areas:

``` text
features/
  companies/
  branches/
  warehouses/
  vehicles/
  users/
  governance/
  roles/
  permissions/
  inventory/
  stock-transfers/
```

Core capabilities:

-   Company context selection where the user has multiple company
    memberships.
-   Branch context selection where the user has multiple active branch
    allocations.
-   Company administration screens.
-   Branch administration screens.
-   Warehouse management.
-   Vehicle management.
-   User company membership management.
-   User branch allocation management.
-   Governance member and individual permission management.
-   Company and branch role management.
-   Stock location views.
-   Transfer request, approval, dispatch and receipt screens.

Rules:

-   Show only contexts the user is authorized to access.
-   A branch administrator must not see or operate another branch's
    role/allocation management.
-   A company user with delegated permissions sees only the company
    functions granted to them.
-   UI visibility is a usability feature, not a security boundary.
-   Every API operation must independently authorize the request.
-   Do not store trusted permissions or tenant ownership solely in local
    storage.
-   Handle stale context and revoked access gracefully.

Use the project's Angular version and conventions. Do not upgrade
Angular as part of this project unless separately approved.

# 16. Audit and security

Audit at least:

-   Company creation and changes.
-   Branch creation, changes and deactivation.
-   Warehouse changes.
-   Vehicle creation, assignment and reassignment.
-   User company membership changes.
-   User branch allocation creation, change and closure.
-   Primary branch changes.
-   Governance membership changes.
-   Individual governance permission changes.
-   Role creation and changes.
-   Role permission changes.
-   User role assignments.
-   Stock transfers and state transitions.
-   Inventory adjustments and reversals.
-   Purchase/sales stock posting references.

Audit records should capture: - Company context. - Actor user. -
Action. - Target entity and ID. - Timestamp. - Relevant before/after
values or a safe structured change representation. - Correlation/request
identifier where available. - Reason/comment for sensitive operations
where required.

Do not log passwords, tokens, payment secrets or unnecessary sensitive
data.

Consider PostgreSQL Row-Level Security only as defence in depth after
reviewing connection pooling, session context, migrations and background
jobs. RLS must not replace application authorization and must not be
introduced without integration tests proving that tenant context cannot
leak between pooled connections.

# 17. Database constraints, indexes and migration strategy

## 17.1 Integrity

Use foreign keys and database constraints to enforce:

-   Branch belongs to company.
-   Warehouse belongs to company and optional branch belongs to same
    company.
-   Vehicle belongs to company and optional assigned branch belongs to
    same company.
-   Stock location belongs to company and optional branch belongs to
    same company.
-   User branch assignment belongs to the same company as user-company
    membership.
-   Branch role belongs to the same company as its branch.
-   User role assignment is compatible with role scope and membership.
-   Inventory location belongs to the same company as the transaction.
-   Transfer source and destination belong to the same company.

Where EF Core conventions cannot express a composite relationship
safely, use explicit Fluent API configuration and reviewed migrations.

## 17.2 Indexing

Evaluate indexes for actual access patterns, including:

-   CompanyId on tenant-owned tables.
-   CompanyId + BranchId.
-   UserId + CompanyId membership.
-   MembershipId + active assignment state.
-   CompanyId + BranchId + role name.
-   CompanyId + StockLocationId + ProductId + BatchId + UnitId.
-   Transfer status and created/updated timestamps.
-   Movement reference type and reference ID.
-   Movement location and date.
-   Audit target and timestamp.

Avoid adding every conceivable index. Check query plans and write
overhead.

## 17.3 Existing data migration

The current system is a single-office setup. The migration plan must:

1.  Identify the existing business/company identity.
2.  Create one default company.
3.  Create one default branch if the existing business operates as one
    branch.
4.  Associate existing company-owned records with the new company.
5.  Associate existing branch-operational records with the default
    branch where appropriate.
6.  Associate existing users with the company.
7.  Assign existing users to the default branch only when supported by
    actual existing business semantics; do not assume every company-wide
    user is branch-only.
8.  Create stock locations for existing stock-holding entities.
9.  Associate existing warehouses with the company and optional branch.
10. Migrate existing stock balances to the appropriate stock locations.
11. Preserve product, unit, batch and barcode references.
12. Preserve purchase, sales, return, payment and financial transaction
    history.
13. Preserve audit history.
14. Reconcile opening stock balances against the existing system.
15. Verify foreign keys and company consistency before enforcing new
    constraints.

Do not delete and recreate historical transactions.

Do not fabricate historical branch allocation data that the existing
database does not contain. Where the old system has insufficient
information, use a documented default mapping and record the migration
assumption.

## 17.4 Migration safety

-   Review every generated migration.
-   Avoid destructive schema operations without explicit approval.
-   Take or verify a restorable database backup before production
    migration.
-   Test migration on a representative copy of existing data.
-   Define rollback or forward-fix strategy.
-   Keep migrations small and phase-aligned.
-   Do not edit already-applied production migrations.
-   Verify migration status in CI/CD and deployment environments.
-   Do not run production migration commands without explicit
    authorization.

# 18. Testing requirements

Tests are mandatory in every phase.

## 18.1 Backend unit tests

Test business rules such as:

-   Company/branch relationship validation.
-   User membership rules.
-   Multiple branch allocations.
-   Primary branch uniqueness.
-   Assignment history closure.
-   Governance permission evaluation.
-   Company and branch role scope.
-   Permission delegation restrictions.
-   Transfer state transitions.
-   Stock quantity calculations.

## 18.2 Integration tests

Use the project's existing test setup and a real PostgreSQL-compatible
test environment where database behavior matters.

Test:

-   Cross-company data isolation.
-   Cross-branch role isolation.
-   Branch administrator cannot manage another branch's roles.
-   Branch administrator cannot change user allocations.
-   Company-level delegated user can manage only permitted company
    resources.
-   Governance member can perform only individually assigned governance
    permissions.
-   User cannot grant themselves higher privileges.
-   User with multiple branch allocations can access only allocated
    branches and permitted operations.
-   Revoked branch assignment removes access.
-   Historical assignments remain available.
-   Company-wide membership is distinct from branch allocation.
-   Warehouse/vehicle/location company integrity.
-   Stock transfer dispatch and receipt.
-   Partial receipt and discrepancy handling.
-   Duplicate posting protection.
-   Concurrent stock issue/transfer protection.
-   Purchase/sales/return integration.
-   Migration of existing records and opening balances.

## 18.3 Frontend tests

Test:

-   Context selection.
-   Navigation visibility.
-   Branch allocation screens.
-   Role scope selection and filtering.
-   Governance permission screens.
-   Forbidden action handling.
-   Empty/loading/error states.
-   API integration and regression of existing screens.

## 18.4 Regression

Existing sales, purchase, return, inventory, barcode, batch, payment and
reporting workflows must remain operational.

Do not mark a phase complete because the new feature works if existing
core workflows have regressed.

# 19. Incremental implementation plan

Execute the phases in order. A phase may be split into smaller
sub-phases when the existing codebase requires it. Do not combine phases
merely to finish faster.

## Phase 0 --- Repository audit and implementation plan

Scope: - Inspect all relevant backend/frontend/database code. - Map
current entities, relationships and workflows. - Identify existing
authorization and inventory implementation. - Identify data migration
risks. - Identify affected APIs and Angular screens. - Identify existing
tests and missing test coverage.

Deliverables: - Current-state architecture report. - Entity and
relationship map. - Existing workflow impact map. - Migration risk
assessment. - Proposed target mapping. - Phase-specific implementation
plan. - ADRs for material design choices.

No broad implementation changes in this phase.

Exit criteria: - Current behavior is understood. - Existing data model
is documented. - Risks and assumptions are listed. - Implementation plan
is reviewed and approved.

## Phase 1 --- Company foundation

Scope: - Add or adapt Company entity. - Add company relationships to
tenant-owned records in a controlled manner. - Establish company context
resolution. - Establish company-level data filtering. - Add company CRUD
only as required by existing product administration. - Create and test
the default-company migration.

Exit criteria: - Existing data belongs to the correct company. - Company
isolation tests pass. - Existing core workflows still pass. - No
cross-company query path is known to bypass company scope.

## Phase 2 --- Branch foundation

Scope: - Add or adapt Branch entity. - Enforce company/branch
relationships. - Support multiple branches. - Add branch
activation/deactivation. - Add branch context resolution. - Add branch
data access foundations.

Exit criteria: - A company can have multiple branches. - Branches cannot
be linked to another company. - Existing single-branch data is mapped
safely. - Authorization and regression tests pass.

## Phase 3 --- Stock location foundation

Scope: - Add or adapt StockLocation. - Define location types and
ownership rules. - Create branch stock locations. - Create company-level
stock locations where needed. - Add database constraints and indexes.

Exit criteria: - Every internal stock-holding location has a valid stock
location. - Company and branch consistency is enforced. - No inventory
posting is refactored prematurely without a reviewed plan.

## Phase 4 --- Warehouse and vehicle integration

Scope: - Support company-level warehouses. - Support branch-level
warehouses. - Link warehouses to stock locations. - Add vehicle master
data. - Link vehicles to stock locations. - Support optional branch
assignment and reassignment validation.

Exit criteria: - Both warehouse ownership models work. - Vehicles can
hold inventory through stock locations. - Reassignment cannot silently
move stock. - Tests and migration checks pass.

## Phase 5 --- User company membership and branch allocation

Scope: - Adapt existing identity/user model. - Add or adapt user-company
membership. - Add historical user-branch assignment records. - Support
multiple active branch allocations. - Support one active primary branch
per company membership. - Implement company-wide versus assigned-branch
access scope. - Implement authorized company-level allocation
management. - Prevent branch administrators from changing allocations. -
Add audit events and access-cache invalidation.

Exit criteria: - Users can be allocated to multiple branches. -
Allocation history is retained. - Company-wide membership is explicit. -
Branch administrators cannot modify allocations. - Revoked access is
effective. - No Employee/HR entity has been introduced.

## Phase 6 --- Platform separation and company governance

Scope: - Separate platform administration from company governance. -
Add/adapt company governance membership. - Support multiple owners and
board members. - Implement individually assigned governance
permissions. - Implement delegated company administration. - Protect
sensitive governance operations. - Add governance audit events.

Exit criteria: - Platform Super Admin is not automatically a company
owner. - Company governance is isolated to its company. - Individual
governance permissions are enforced. - Titles alone do not grant
unrestricted permissions. - Self-escalation is prevented.

## Phase 7 --- Scoped RBAC

Scope: - Adapt permission catalogue. - Implement company-scoped roles. -
Implement branch-scoped roles. - Implement role-permission
assignments. - Implement user-role assignments. - Validate assignment
scope and membership. - Implement permission delegation limits. -
Implement effective permission evaluation. - Add role-management APIs
and Angular screens as appropriate.

Exit criteria: - Branch 1 cannot manage Branch 2 roles. - Company roles
cannot cross company boundaries. - Branch roles cannot become
company-wide roles. - Users cannot grant themselves higher
permissions. - Governance and operational RBAC remain separate. -
Authorization integration tests pass.

## Phase 8 --- Location-based inventory refactoring

Scope: - Inspect and adapt inventory balance model. - Add
StockLocationId to the relevant inventory structures. - Implement or
adapt stock movement ledger. - Preserve product, batch, barcode and unit
traceability. - Migrate existing stock balances. - Centralize
transactional stock posting. - Implement reconciliation and concurrency
controls.

Exit criteria: - Stock is attributable to a location. - Ledger and
balances reconcile. - Existing batch/barcode references are preserved. -
Existing purchase/sales behavior remains correct. - Concurrency and
duplicate-post tests pass.

## Phase 9 --- Stock transfer lifecycle

Scope: - Transfer request. - Approval policy. - Dispatch. - Transit. -
Partial receipt. - Damage, shortage and excess resolution. - Completion
and cancellation rules. - Audit and transfer history.

Exit criteria: - All supported internal location pairs work. - No
cross-company transfer is possible. - Transit stock is not available for
ordinary sales. - Partial receipt is correct. - No quantity is deducted
or credited twice. - Stock reconciliation tests pass.

## Phase 10 --- Purchase, sales, returns and vehicle operations

Scope: - Integrate purchase receiving with destination stock location. -
Integrate sales with authorized source location. - Integrate purchase
returns. - Integrate sales returns. - Implement vehicle loading. -
Implement van sales. - Implement vehicle stock returns and
discrepancies. - Preserve existing financial and document consistency.

Exit criteria: - All existing core workflows use the unified stock
movement service. - Sale, purchase and return transactions remain
financially consistent. - Van stock is traceable. - Batch and barcode
rules remain correct. - End-to-end regression tests pass.

## Phase 11 --- Angular integration and final verification

Scope: - Complete company/branch context selection. - Complete company
and branch administration screens. - Complete warehouse and vehicle
screens. - Complete user membership and branch allocation screens. -
Complete governance screens. - Complete scoped role and permission
screens. - Complete stock location and transfer screens. - Verify error
handling and forbidden states. - Run full regression suite. - Prepare
deployment and migration runbook.

Exit criteria: - Backend and frontend functionality is integrated. -
Authorization is enforced server-side. - Existing core workflows pass. -
Migration and deployment procedures are documented. - Known limitations
and operational risks are recorded.

# 20. Mandatory Claude Code phase execution protocol

Claude Code must execute only one approved phase at a time.

For every phase:

## Before implementation

1.  Read this specification.
2.  Read `CLAUDE.md` and relevant project instructions.
3.  Check Git status and current branch.
4.  Identify user changes and uncommitted work.
5.  Inspect the exact code paths involved.
6.  Reconfirm dependencies and phase exit criteria.
7.  State the planned files, database changes, APIs and tests.
8.  Identify risks and assumptions.

## During implementation

1.  Work only within the approved phase scope.
2.  Follow existing architecture.
3.  Make small, reviewable changes.
4.  Add or update tests with the implementation.
5.  Generate and inspect EF Core migrations.
6.  Keep the solution compiling.
7.  Run focused tests frequently.
8.  Do not bypass failing tests by weakening assertions.
9.  Do not change unrelated business behavior.
10. Record necessary deviations and decisions.

## Before declaring completion

Run applicable:

-   Backend build.
-   Backend unit tests.
-   Backend integration tests.
-   EF migration validation.
-   Frontend build.
-   Frontend tests.
-   Relevant regression tests.
-   Git diff review.

Report actual commands and results. Never claim a test passed if it was
not executed.

## Required phase completion report

Use this structure:

``` text
PHASE:
STATUS: Completed / Blocked / Partially completed

1. Summary
2. Requirements implemented
3. Files created
4. Files modified
5. Database changes
6. Migration details
7. API changes
8. Frontend changes
9. Tests executed
10. Test results
11. Security review
12. Regression results
13. Known issues
14. Risks and assumptions
15. Documentation updated
16. Git status and suggested checkpoint
17. Recommended next phase
```

Stop after the report. Do not begin the next phase until the user
explicitly authorizes it.

If blocked: - Explain the exact blocker. - Do not silently skip the
requirement. - Do not proceed to dependent phases. - Offer the smallest
safe resolution.

# 21. Git and recovery strategy

Follow the repository's existing branching and commit conventions.

Suggested feature branches or checkpoints:

-   `feature/multi-company-foundation`
-   `feature/multi-branch-management`
-   `feature/stock-location-foundation`
-   `feature/user-branch-access`
-   `feature/company-governance`
-   `feature/scoped-rbac`
-   `feature/location-inventory`
-   `feature/stock-transfer-workflow`

Before each phase: - Check current branch and working tree. - Do not
overwrite unrelated user changes. - Do not reset or discard user work. -
Do not force-push. - Do not create commits unless the user or repository
instructions authorize them.

After each completed phase: - Review the diff. - Verify tests. - Verify
migration status. - Provide a recommended checkpoint commit message. -
Record the current phase status in project documentation.

# 22. Definition of done

The overall initiative is complete only when all applicable criteria are
satisfied:

## Organization

-   Multiple companies are supported.
-   Each company supports multiple branches.
-   Company-level and branch-level warehouses are supported.
-   Vehicles have stock locations.
-   Company/branch ownership constraints are enforced.

## Users and governance

-   User company membership is explicit.
-   Users can have multiple active branch allocations.
-   One primary branch per membership is enforced where applicable.
-   Allocation history is retained.
-   Branch administrators cannot manage allocations.
-   Company governance can manage access according to individually
    assigned permissions.
-   Multiple owners and board members are supported.
-   Platform Super Admin is separate from company governance.
-   No employee/HR module has been introduced.

## Authorization

-   Company roles are company-scoped.
-   Branch roles are branch-scoped.
-   Branches cannot manage one another's roles.
-   Company data is isolated.
-   Permission delegation is restricted.
-   Privilege escalation is prevented.
-   Governance and operational permissions are separate.

## Inventory

-   Inventory is location-based.
-   Warehouse and vehicle stock is traceable.
-   Stock movements preserve batch/unit/location references.
-   Transfers support transit and partial receipt.
-   Purchase, sales, returns and van operations use unified stock
    posting.
-   Inventory ledger and balances reconcile.

## Quality and operations

-   Existing historical transactions are preserved.
-   Migrations are tested and documented.
-   Backend and frontend tests pass.
-   Security and regression tests pass.
-   Deployment and recovery procedures are documented.
-   No unresolved critical issue remains.

# 23. Final instruction to Claude Code

Treat this document as the approved target architecture and phased
implementation contract.

Do not attempt a broad rewrite.

Start with Phase 0 only.

Inspect the real Sellora Billing repository and report the current
state, discrepancies, risks and proposed implementation plan.

Do not implement Phase 1 until the user explicitly approves the Phase 0
report.

When the existing implementation conflicts with this document, do not
guess. Explain the conflict, identify the impact and propose the
smallest safe decision for approval.

Preserve working Sellora functionality, data integrity, security
boundaries and the user's existing changes above implementation speed.

------------------------------------------------------------------------

END OF SPECIFICATION
