-- Idempotent seed of the permission catalogue (Phase 4 O-4). Mirrors PermissionCatalog.All; rows already present are left untouched
-- (ON CONFLICT ("Code") DO NOTHING), so it is safe to re-run on every release. Ids are random (gen_random_uuid()), like the
-- application seeder (Guid.NewGuid()); nothing references them by value.
-- It does not grant anything to roles: Administrator receives every catalogue code through PermissionResolver.
-- Run:  psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f scripts/sql/seed-permissions.sql
-- Regenerate when PermissionCatalog changes (a test guards against drift).

INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description")
SELECT gen_random_uuid(), v."Code", v."Module", v."Action", v."Description"
FROM (VALUES
  ('Users.View', 'Administration', 'View', 'View the user list and user details.'),
  ('Users.Create', 'Administration', 'Create', 'Invite/create new users.'),
  ('Users.Edit', 'Administration', 'Edit', 'Edit user details and role assignment.'),
  ('Users.Disable', 'Administration', 'Disable', 'Activate or deactivate a user account.'),
  ('Users.ResetPassword', 'Administration', 'ResetPassword', 'Trigger a password reset for a user.'),
  ('Roles.View', 'Administration', 'View', 'View roles and their permission mappings.'),
  ('Roles.Create', 'Administration', 'Create', 'Create new roles.'),
  ('Roles.Edit', 'Administration', 'Edit', 'Edit a role''s details and permission mapping.'),
  ('Roles.Duplicate', 'Administration', 'Duplicate', 'Duplicate an existing role.'),
  ('Roles.Disable', 'Administration', 'Disable', 'Activate or deactivate a role.'),
  ('Company.Users.AssignRoles', 'Administration', 'AssignRoles', 'Assign and end company-wide roles for users (company-wide administrators only).'),
  ('Branch.Roles.View', 'Administration', 'View', 'View the roles scoped to the branch.'),
  ('Branch.Roles.Manage', 'Administration', 'Manage', 'Create, edit, duplicate and deactivate roles scoped to the branch.'),
  ('Branch.Users.AssignRoles', 'Administration', 'AssignRoles', 'Assign and end branch roles for users allocated to the branch.'),
  ('Permissions.View', 'Administration', 'View', 'View the read-only system permission catalogue.'),
  ('Company.Branches.View', 'Administration', 'View', 'View the company''s branches.'),
  ('Company.Branches.Create', 'Administration', 'Create', 'Add new branches.'),
  ('Company.Branches.Edit', 'Administration', 'Edit', 'Edit branch details, GSTIN and invoice prefix.'),
  ('Company.Branches.Disable', 'Administration', 'Disable', 'Activate or deactivate a branch.'),
  ('Company.Users.ManageBranchAccess', 'Administration', 'ManageBranchAccess', 'Manage a user''s company membership and branch allocations (company-wide administrators only).'),
  ('Company.Warehouses.View', 'Administration', 'View', 'View the company''s warehouses.'),
  ('Company.Warehouses.Create', 'Administration', 'Create', 'Add company or branch warehouses.'),
  ('Company.Warehouses.Edit', 'Administration', 'Edit', 'Rename warehouses.'),
  ('Company.Warehouses.Disable', 'Administration', 'Disable', 'Activate or deactivate a warehouse.'),
  ('Company.Vehicles.View', 'Administration', 'View', 'View the company''s vehicles.'),
  ('Company.Vehicles.Create', 'Administration', 'Create', 'Add vehicles.'),
  ('Company.Vehicles.Edit', 'Administration', 'Edit', 'Edit vehicle details and branch assignment.'),
  ('Company.Vehicles.Disable', 'Administration', 'Disable', 'Activate or deactivate a vehicle.'),
  ('Masters.View', 'Administration', 'View', 'View units, taxes, currencies, exchange rates and business settings.'),
  ('Masters.Edit', 'Administration', 'Edit', 'Edit units, taxes, currencies, exchange rates and business settings.'),
  ('Inventory.View', 'Inventory', 'View', 'View stock overview, details and movement.'),
  ('Inventory.Create', 'Inventory', 'Create', 'Create new products.'),
  ('Inventory.Edit', 'Inventory', 'Edit', 'Edit existing products.'),
  ('Inventory.Delete', 'Inventory', 'Delete', 'Delete products that have never been used in a transaction.'),
  ('Inventory.Adjust', 'Inventory', 'Adjust', 'Post stock adjustments (damage, loss, correction, etc.).'),
  ('Inventory.Price', 'Inventory', 'Price', 'Configure product units and prices.'),
  ('Inventory.Categories', 'Inventory', 'Manage categories', 'Create, edit, activate and deactivate product categories and subcategories.'),
  ('StockLocations.View', 'Inventory', 'View locations', 'View stock locations (branch stores, warehouses, vehicles, in-transit).'),
  ('StockTransfers.View', 'Inventory', 'View transfers', 'View stock transfers.'),
  ('StockTransfers.Create', 'Inventory', 'Create transfers', 'Create, edit and submit stock transfer requests.'),
  ('StockTransfers.Approve', 'Inventory', 'Approve transfers', 'Approve or reject stock transfer requests.'),
  ('StockTransfers.Dispatch', 'Inventory', 'Dispatch transfers', 'Dispatch approved stock transfers from the source location.'),
  ('StockTransfers.Receive', 'Inventory', 'Receive transfers', 'Receive dispatched stock transfers at the destination location.'),
  ('StockTransfers.Cancel', 'Inventory', 'Cancel transfers', 'Cancel stock transfers before they are dispatched.'),
  ('StockTransfers.Resolve', 'Inventory', 'Resolve transfers', 'Resolve in-transit shortages by writing them off or returning them to the source (company-wide only).'),
  ('Shipments.View', 'Inventory', 'View shipments', 'View shipments, consignments and their allocations.'),
  ('Shipments.Plan', 'Inventory', 'Plan shipments', 'Create, edit, allocate stock to, assign vehicles to and cancel shipments and consignments.'),
  ('Shipments.Load', 'Inventory', 'Load shipments', 'Record loading of consignments and mark shipments ready to dispatch.'),
  ('Reservations.Release', 'Inventory', 'Release reservations', 'Release stock reservations that are no longer needed (company-wide only).'),
  ('Suppliers.View', 'Purchase', 'View', 'View the supplier list.'),
  ('Suppliers.Create', 'Purchase', 'Create', 'Add new suppliers.'),
  ('Suppliers.Edit', 'Purchase', 'Edit', 'Edit supplier details.'),
  ('Suppliers.Disable', 'Purchase', 'Disable', 'Activate or deactivate a supplier.'),
  ('Purchase.View', 'Purchase', 'View', 'View purchase orders.'),
  ('Purchase.Create', 'Purchase', 'Create', 'Create new purchase orders.'),
  ('Purchase.Edit', 'Purchase', 'Edit', 'Edit draft purchase orders.'),
  ('Purchase.Pay', 'Purchase', 'Pay', 'Record payments against purchase orders.'),
  ('Purchase.Return', 'Purchase', 'Return', 'Return goods to a supplier.'),
  ('Purchase.Refund', 'Purchase', 'Refund', 'Record supplier refunds and reverse purchase payments.'),
  ('Purchase.Cancel', 'Purchase', 'Cancel', 'Cancel a completed purchase order.'),
  ('Customers.View', 'Sales', 'View', 'View the customer list.'),
  ('Customers.Create', 'Sales', 'Create', 'Add new customers.'),
  ('Customers.Edit', 'Sales', 'Edit', 'Edit customer details.'),
  ('Customers.Disable', 'Sales', 'Disable', 'Activate or deactivate a customer.'),
  ('Sales.View', 'Sales', 'View', 'View sales invoices.'),
  ('Sales.Create', 'Sales', 'Create', 'Create sales through POS billing.'),
  ('Sales.PriceOverride', 'Sales', 'PriceOverride', 'Charge a price other than the list price, or give a discount, at the POS.'),
  ('Sales.Edit', 'Sales', 'Edit', 'Edit an in-progress sale.'),
  ('Sales.Receive', 'Sales', 'Receive', 'Record payments received against sales invoices.'),
  ('Sales.Return', 'Sales', 'Return', 'Take back goods from a customer.'),
  ('Sales.Refund', 'Sales', 'Refund', 'Refund customers and reverse sales payments.'),
  ('Sales.Cancel', 'Sales', 'Cancel', 'Cancel a completed sales invoice.'),
  ('Expenses.View', 'Accounts', 'View', 'View operating expenses.'),
  ('Expenses.Create', 'Accounts', 'Create', 'Record operating expenses.'),
  ('Expenses.Edit', 'Accounts', 'Edit', 'Edit an expense''s category, reference and notes.'),
  ('Expenses.Cancel', 'Accounts', 'Cancel', 'Cancel an expense.'),
  ('Expenses.Categories', 'Accounts', 'Categories', 'Create, rename and deactivate expense categories.'),
  ('FinancialTransactions.View', 'Accounts', 'View', 'View the financial transaction history.'),
  ('CashBank.View', 'Accounts', 'View', 'View cash and bank accounts, balances and account history.'),
  ('CashBank.Manage', 'Accounts', 'Manage', 'Create, edit and deactivate cash and bank accounts.'),
  ('CashBank.Transfer', 'Accounts', 'Transfer', 'Transfer money between accounts, including cash deposits and withdrawals.'),
  ('CashBank.Reconcile', 'Accounts', 'Reconcile', 'Reconcile account transactions against statements.'),
  ('CashBank.Fund', 'Accounts', 'Fund', 'Record funding (capital, loan, deposit, customer receipt) into cash and bank accounts.'),
  ('CashBank.Reverse', 'Accounts', 'Reverse', 'Reverse funding, transfers and opening balances posted in error.'),
  ('Reports.Stock', 'Reports', 'View', 'View the current stock report.'),
  ('Reports.Movement', 'Reports', 'View', 'View the stock movement report.'),
  ('Reports.Purchase', 'Reports', 'View', 'View the purchase report.'),
  ('Reports.Sales', 'Reports', 'View', 'View the sales report.'),
  ('Reports.Accounts', 'Reports', 'View', 'View the Accounts reports: payments, outstanding balances, expenses, cash flow, cash & bank and payment methods.'),
  ('Reports.Profit', 'Reports', 'View', 'View the profit/margin summary report.')
) AS v("Code", "Module", "Action", "Description")
ON CONFLICT ("Code") DO NOTHING;

-- Phase 6: governance catalogue (separate table, never part of "Permissions"; the CK_Permissions_NotGovernance CHECK forbids these codes there).
-- Mirrors GovernancePermissionCatalog.All. Ids are deterministic (md5, identical to the migration and the application seeder); rows already
-- present are left untouched. Insert-only: no row is ever updated or removed by this script.
INSERT INTO "GovernancePermissions" ("Id", "Code", "Description")
SELECT md5('govperm:' || v."Code")::uuid, v."Code", v."Description"
FROM (VALUES
  ('Company.Governance.View', 'View governance members, their titles and grants, and the governance audit trail.'),
  ('Company.Governance.ManageMembers', 'Add, retitle and end governance members other than owners.'),
  ('Company.Governance.ManageOwners', 'Add, retitle, end or change the grants of Owner-title governance members.'),
  ('Company.Governance.AssignPermissions', 'Grant and revoke governance permissions (only codes the actor holds).'),
  ('Company.Governance.ManageAdministrators', 'Grant and remove the Administrator role (delegated company administration).')
) AS v("Code", "Description")
ON CONFLICT ("Code") DO NOTHING;
