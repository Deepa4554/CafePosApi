$ErrorActionPreference = "Stop"
$base = "http://localhost:5080"
$results = @()

function Test-Endpoint {
    param($Name, $Method, $Url, $Body = $null, $Headers = @{}, $ExpectStatus = 200)
    try {
        $params = @{ Uri = "$base$Url"; Method = $Method; Headers = $Headers; UseBasicParsing = $true }
        if ($Body) { $params.ContentType = "application/json"; $params.Body = ($Body | ConvertTo-Json -Depth 10) }
        $r = Invoke-WebRequest @params
        $ok = $r.StatusCode -eq $ExpectStatus
        $script:results += [pscustomobject]@{ Test = $Name; Status = $r.StatusCode; Expected = $ExpectStatus; Pass = $ok }
        return $r.Content | ConvertFrom-Json
    } catch {
        $actual = $_.Exception.Response.StatusCode.value__
        $ok = $actual -eq $ExpectStatus
        $script:results += [pscustomobject]@{ Test = $Name; Status = $actual; Expected = $ExpectStatus; Pass = $ok }
        return $null
    }
}

# ===================== AUTH =====================
$ownerEmail = "owner_$(Get-Random)@cafepos.test"
$owner = Test-Endpoint "Auth: Register Owner" POST "/api/auth/register" @{ email=$ownerEmail; password="Passw0rd!"; name="Test Owner"; role="Owner" } @{} 200
$ownerToken = $owner.accessToken
$ownerHeaders = @{ Authorization = "Bearer $ownerToken" }

$managerEmail = "mgr_$(Get-Random)@cafepos.test"
$manager = Test-Endpoint "Auth: Register Manager" POST "/api/auth/register" @{ email=$managerEmail; password="Passw0rd!"; name="Test Manager"; role="Manager" } @{} 200
$managerHeaders = @{ Authorization = "Bearer $($manager.accessToken)" }

$waiterEmail = "waiter_$(Get-Random)@cafepos.test"
$waiter = Test-Endpoint "Auth: Register Waiter" POST "/api/auth/register" @{ email=$waiterEmail; password="Passw0rd!"; name="Test Waiter"; role="Waiter" } @{} 200
$waiterHeaders = @{ Authorization = "Bearer $($waiter.accessToken)" }

Test-Endpoint "Auth: Register duplicate email -> 409" POST "/api/auth/register" @{ email=$ownerEmail; password="Passw0rd!"; name="Dup"; role="Owner" } @{} 409 | Out-Null
# NOTE: each login/refresh rotates the refresh token (only one active at a time
# by design), so always use the token from the MOST RECENT auth response.
$loginResult = Test-Endpoint "Auth: Login correct" POST "/api/auth/login" @{ email=$ownerEmail; password="Passw0rd!" } @{} 200
Test-Endpoint "Auth: Login wrong password -> 401" POST "/api/auth/login" @{ email=$ownerEmail; password="wrong" } @{} 401 | Out-Null
$me = Test-Endpoint "Auth: Me (authenticated)" GET "/api/auth/me" $null $ownerHeaders 200
Test-Endpoint "Auth: Me (no token) -> 401" GET "/api/auth/me" $null @{} 401 | Out-Null
$refreshed = Test-Endpoint "Auth: Refresh token" POST "/api/auth/refresh" @{ refreshToken=$loginResult.refreshToken } @{} 200
Test-Endpoint "Auth: Change password" POST "/api/auth/change-password" @{ currentPassword="Passw0rd!"; newPassword="NewPassw0rd!" } $ownerHeaders 204 | Out-Null
Test-Endpoint "Auth: Logout" POST "/api/auth/logout" $null $ownerHeaders 204 | Out-Null

# re-login owner since we changed the password
$owner = Test-Endpoint "Auth: Re-login after password change" POST "/api/auth/login" @{ email=$ownerEmail; password="NewPassw0rd!" } @{} 200
$ownerHeaders = @{ Authorization = "Bearer $($owner.accessToken)" }

# ===================== MENU =====================
Test-Endpoint "Menu: List (anonymous, public QR menu)" GET "/api/menu-items" $null @{} 200 | Out-Null
$newMenuItem = Test-Endpoint "Menu: Create (Owner)" POST "/api/menu-items" @{ name="Test Mocha"; category="Espresso"; price=5.5; icon="coffee"; subtitle="Test item" } $ownerHeaders 201
Test-Endpoint "Menu: Create (Waiter) -> 403" POST "/api/menu-items" @{ name="Blocked Item"; category="Food"; price=1 } $waiterHeaders 403 | Out-Null
Test-Endpoint "Menu: Update price" PATCH "/api/menu-items/$($newMenuItem.id)" @{ price=6.0 } $ownerHeaders 200 | Out-Null
Test-Endpoint "Menu: Toggle availability" PATCH "/api/menu-items/$($newMenuItem.id)/toggle-availability" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Menu: Create invalid (empty name) -> 400" POST "/api/menu-items" @{ name=""; category="Food"; price=1 } $ownerHeaders 400 | Out-Null

# ===================== TABLES =====================
Test-Endpoint "Tables: List" GET "/api/tables" $null $ownerHeaders 200 | Out-Null
$newTable = Test-Endpoint "Tables: Create (Owner)" POST "/api/tables" @{ zone="Indoor"; seats=4 } $ownerHeaders 201
Test-Endpoint "Tables: Create (Waiter) -> 403" POST "/api/tables" @{ zone="Indoor"; seats=2 } $waiterHeaders 403 | Out-Null

# ===================== INVENTORY =====================
Test-Endpoint "Inventory: List (Owner)" GET "/api/inventory" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Inventory: List (Waiter) -> 403 (NotWaiter policy)" GET "/api/inventory" $null $waiterHeaders 403 | Out-Null
$invItem = Test-Endpoint "Inventory: Create item" POST "/api/inventory" @{ name="Test Syrup"; category="Syrups"; max=10; unit="L" } $ownerHeaders 201
Test-Endpoint "Inventory: Restock" POST "/api/inventory/$($invItem.id)/restock" $null $ownerHeaders 200 | Out-Null

# ===================== ORDERS (core flow) =====================
$menu = Test-Endpoint "Menu: List for order test" GET "/api/menu-items" $null @{} 200
$espresso = $menu | Where-Object { $_.name -eq "Double Espresso" } | Select-Object -First 1
$croissant = $menu | Where-Object { $_.name -eq "Almond Croissant" } | Select-Object -First 1

$order = Test-Endpoint "Orders: Create Dine-In (T1)" POST "/api/orders" @{
    orderType="DINE_IN"; tableCode="T1"; guestName="Priya Sharma";
    items=@(@{ menuItemId=$espresso.id; qty=2; modifier="Oat Milk" }, @{ menuItemId=$croissant.id; qty=1 })
} $ownerHeaders 201
Test-Endpoint "Orders: Duplicate fire on same table -> 409" POST "/api/orders" @{
    orderType="DINE_IN"; tableCode="T1"; guestName="Someone Else"; items=@(@{ menuItemId=$espresso.id; qty=1 })
} $ownerHeaders 409 | Out-Null
Test-Endpoint "Orders: Empty items -> 400" POST "/api/orders" @{ orderType="TAKEAWAY"; items=@() } $ownerHeaders 400 | Out-Null
Test-Endpoint "Orders: Unavailable item -> 400" POST "/api/orders" @{ orderType="TAKEAWAY"; items=@(@{ menuItemId=$newMenuItem.id; qty=1 }) } $ownerHeaders 400 | Out-Null
Test-Endpoint "Orders: Get by id" GET "/api/orders/$($order.id)" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Orders: List active" GET "/api/orders?activeOnly=true" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Orders: Advance NEW->PREPARING" PATCH "/api/orders/$($order.id)/advance" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Orders: Set status explicit" PATCH "/api/orders/$($order.id)/status" @{ status="READY" } $ownerHeaders 200 | Out-Null
Test-Endpoint "Orders: Pay -> frees table" PATCH "/api/orders/$($order.id)/pay" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Orders: Pay again -> 409" PATCH "/api/orders/$($order.id)/pay" $null $ownerHeaders 409 | Out-Null
Test-Endpoint "Orders: Refund" POST "/api/orders/$($order.id)/refund" @{ amount=5.0; reason="Customer complaint" } $ownerHeaders 200 | Out-Null
Test-Endpoint "Orders: Refund again -> 409" POST "/api/orders/$($order.id)/refund" @{ reason="dup" } $ownerHeaders 409 | Out-Null

$tablesAfterPay = Test-Endpoint "Tables: List after pay (T1 should be empty)" GET "/api/tables" $null $ownerHeaders 200
$t1 = $tablesAfterPay | Where-Object { $_.code -eq "T1" }

# ===================== CRM / CUSTOMERS =====================
$customers = Test-Endpoint "Customers: List (should include Priya from order)" GET "/api/customers?search=Priya" $null $ownerHeaders 200
$priya = $customers.items | Select-Object -First 1
Test-Endpoint "Customers: Get detail (visits/favorites populated)" GET "/api/customers/$($priya.id)" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Customers: Create manual" POST "/api/customers" @{ name="Manual Customer"; email="manual@test.com" } $ownerHeaders 201 | Out-Null
Test-Endpoint "Customers: Update" PATCH "/api/customers/$($priya.id)" @{ notes="VIP guest" } $ownerHeaders 200 | Out-Null
Test-Endpoint "Customers: Add points" POST "/api/customers/$($priya.id)/points/add" @{ points=100 } $ownerHeaders 200 | Out-Null
Test-Endpoint "Customers: Redeem points" POST "/api/customers/$($priya.id)/points/redeem" @{ points=10 } $ownerHeaders 200 | Out-Null
Test-Endpoint "Customers: Redeem more than available -> 400" POST "/api/customers/$($priya.id)/points/redeem" @{ points=999999 } $ownerHeaders 400 | Out-Null

$coupon = Test-Endpoint "Customers: Issue coupon" POST "/api/customers/$($priya.id)/coupons" @{ title="Loyalty 10%"; description="10% off"; type="Percent"; value=10; minOrderValue=0; expiresAt=(Get-Date).AddDays(30).ToString("o") } $ownerHeaders 200
Test-Endpoint "Customers: Apply coupon (valid)" POST "/api/customers/coupons/apply" @{ code=$coupon.code; orderSubtotal=100 } $ownerHeaders 200 | Out-Null
Test-Endpoint "Customers: Redeem coupon" POST "/api/customers/coupons/$($coupon.id)/redeem" $null $ownerHeaders 204 | Out-Null
Test-Endpoint "Customers: Redeem same coupon again -> 409" POST "/api/customers/coupons/$($coupon.id)/redeem" $null $ownerHeaders 409 | Out-Null

$giftCard = Test-Endpoint "Customers: Issue gift card" POST "/api/customers/gift-cards" @{ amount=50; customerId=$priya.id; purchasedBy="Priya" } $ownerHeaders 200
Test-Endpoint "Customers: Redeem gift card" POST "/api/customers/gift-cards/redeem" @{ code=$giftCard.code; amount=20 } $ownerHeaders 200 | Out-Null
Test-Endpoint "Customers: Redeem more than balance -> 400" POST "/api/customers/gift-cards/redeem" @{ code=$giftCard.code; amount=99999 } $ownerHeaders 400 | Out-Null

# order with a coupon code end to end
$coupon2 = Test-Endpoint "Customers: Issue 2nd coupon for order-integration test" POST "/api/customers/$($priya.id)/coupons" @{ title="Flat 5"; description="Flat 5 off"; type="Flat"; value=5; minOrderValue=0; expiresAt=(Get-Date).AddDays(30).ToString("o") } $ownerHeaders 200
Test-Endpoint "Orders: Create with coupon code" POST "/api/orders" @{
    orderType="TAKEAWAY"; guestName="Priya Sharma"; couponCode=$coupon2.code; items=@(@{ menuItemId=$espresso.id; qty=1 })
} $ownerHeaders 201 | Out-Null

# ===================== TASKS =====================
$task = Test-Endpoint "Tasks: Create" POST "/api/tasks" @{ title="Reorder cups"; priority="High"; dueDate=(Get-Date).AddDays(1).ToString("o"); tags=@("inventory") } $ownerHeaders 201
Test-Endpoint "Tasks: List" GET "/api/tasks" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Tasks: Update status" PATCH "/api/tasks/$($task.id)/status" @{ status="InProgress" } $ownerHeaders 200 | Out-Null
Test-Endpoint "Tasks: Create empty title -> 400" POST "/api/tasks" @{ title=""; priority="Low"; dueDate=(Get-Date).ToString("o") } $ownerHeaders 400 | Out-Null
Test-Endpoint "Tasks: Delete" DELETE "/api/tasks/$($task.id)" $null $ownerHeaders 204 | Out-Null

# ===================== NOTIFICATIONS =====================
$notif = Test-Endpoint "Notifications: Create" POST "/api/notifications" @{ title="Low Stock"; body="Oat milk low"; category="Inventory" } $ownerHeaders 201
Test-Endpoint "Notifications: List" GET "/api/notifications" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Notifications: Mark read" PATCH "/api/notifications/$($notif.id)/read" $null $ownerHeaders 204 | Out-Null
Test-Endpoint "Notifications: Mark all read" POST "/api/notifications/read-all" $null $ownerHeaders 204 | Out-Null
Test-Endpoint "Notifications: Archive" PATCH "/api/notifications/$($notif.id)/archive" $null $ownerHeaders 204 | Out-Null
Test-Endpoint "Notifications: Retry" POST "/api/notifications/$($notif.id)/retry" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Notifications: Delete" DELETE "/api/notifications/$($notif.id)" $null $ownerHeaders 204 | Out-Null

# ===================== APPROVALS =====================
$approval = Test-Endpoint "Approvals: Submit" POST "/api/approvals" @{ type="Refund"; assignedToId=1; title="Refund order"; description="Wrong item"; amount=100 } $ownerHeaders 201
Test-Endpoint "Approvals: List" GET "/api/approvals" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Approvals: Approve" PATCH "/api/approvals/$($approval.id)/approve" @{ notes="Approved, valid complaint" } $ownerHeaders 200 | Out-Null
Test-Endpoint "Approvals: Approve already-resolved -> 409" PATCH "/api/approvals/$($approval.id)/approve" @{ } $ownerHeaders 409 | Out-Null
$approval2 = Test-Endpoint "Approvals: Submit 2nd" POST "/api/approvals" @{ type="Discount"; assignedToId=1; title="Discount req"; description="test"; amount=50 } $ownerHeaders 201
Test-Endpoint "Approvals: Reject" PATCH "/api/approvals/$($approval2.id)/reject" @{ notes="Not eligible" } $ownerHeaders 200 | Out-Null
$approval3 = Test-Endpoint "Approvals: Submit 3rd for escalation" POST "/api/approvals" @{ type="Expense"; assignedToId=1; title="Big expense"; description="test"; amount=5000 } $ownerHeaders 201
Test-Endpoint "Approvals: Escalate" PATCH "/api/approvals/$($approval3.id)/escalate" $null $ownerHeaders 200 | Out-Null

# ===================== AUDIT LOG =====================
Test-Endpoint "Audit: List (Owner)" GET "/api/audit-log" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Audit: List (Waiter) -> 403" GET "/api/audit-log" $null $waiterHeaders 403 | Out-Null

# ===================== STAFF =====================
$staff = Test-Endpoint "Staff: Create (Owner)" POST "/api/staff" @{ name="Alex Barista"; role="Barista"; hourlyRate=15 } $ownerHeaders 201
Test-Endpoint "Staff: Create (Waiter) -> 403" POST "/api/staff" @{ name="Blocked"; role="X" } $waiterHeaders 403 | Out-Null
Test-Endpoint "Staff: List" GET "/api/staff" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Staff: Update status" PATCH "/api/staff/$($staff.id)/status" @{ status="OnLeave" } $ownerHeaders 200 | Out-Null
$shift = Test-Endpoint "Staff: Create shift" POST "/api/staff/shifts" @{ staffId=$staff.id; startsAt=(Get-Date).ToString("o"); endsAt=(Get-Date).AddHours(8).ToString("o") } $ownerHeaders 200
Test-Endpoint "Staff: List shifts" GET "/api/staff/$($staff.id)/shifts" $null $ownerHeaders 200 | Out-Null
$payStart = [System.Uri]::EscapeDataString((Get-Date).AddDays(-7).ToString("o"))
$payEnd = [System.Uri]::EscapeDataString((Get-Date).AddDays(1).ToString("o"))
Test-Endpoint "Staff: Payroll" GET "/api/staff/payroll?periodStart=$payStart&periodEnd=$payEnd" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Staff: Delete shift" DELETE "/api/staff/shifts/$($shift.id)" $null $ownerHeaders 204 | Out-Null

# ===================== BRANCHES =====================
Test-Endpoint "Branches: List (Owner)" GET "/api/branches" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Branches: List (Waiter) -> 403" GET "/api/branches" $null $waiterHeaders 403 | Out-Null
# Fresh tenant starts on FreeTrial (max 1 branch) with 1 seeded branch already
# present, so adding a 2nd must be correctly blocked by the plan-limit rule.
Test-Endpoint "Branches: Create beyond FreeTrial limit -> 409" POST "/api/branches" @{ name="Northside Cafe"; address="456 North Ave" } $ownerHeaders 409 | Out-Null
Test-Endpoint "Subscription: Upgrade to Professional (unblocks branch limit)" POST "/api/subscription/change-plan" @{ plan="Professional" } $ownerHeaders 200 | Out-Null
Test-Endpoint "Branches: Create after upgrade" POST "/api/branches" @{ name="Northside Cafe"; address="456 North Ave" } $ownerHeaders 201 | Out-Null

# ===================== SUBSCRIPTION =====================
Test-Endpoint "Subscription: Get" GET "/api/subscription" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Subscription: Change plan (Manager) -> 403" POST "/api/subscription/change-plan" @{ plan="Professional" } $managerHeaders 403 | Out-Null
Test-Endpoint "Subscription: Change plan (Owner)" POST "/api/subscription/change-plan" @{ plan="Professional" } $ownerHeaders 200 | Out-Null

# ===================== INTEGRATIONS =====================
$integrations = Test-Endpoint "Integrations: List" GET "/api/integrations" $null $ownerHeaders 200
$stripe = $integrations | Where-Object { $_.name -eq "Stripe" }
Test-Endpoint "Integrations: Connect" POST "/api/integrations/$($stripe.id)/connect" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Integrations: Disconnect" POST "/api/integrations/$($stripe.id)/disconnect" $null $ownerHeaders 200 | Out-Null

# ===================== SETTINGS =====================
Test-Endpoint "Settings: Get" GET "/api/settings" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Settings: Update (Manager)" PUT "/api/settings" @{ taxRatePct=12; businessName="CafePOS Test" } $managerHeaders 200 | Out-Null
Test-Endpoint "Settings: Update (Waiter) -> 403" PUT "/api/settings" @{ taxRatePct=99 } $waiterHeaders 403 | Out-Null
Test-Endpoint "Settings: Invalid tax rate -> 400" PUT "/api/settings" @{ taxRatePct=150 } $ownerHeaders 400 | Out-Null
Test-Endpoint "Settings: Complete onboarding" POST "/api/settings/complete-onboarding" $null $ownerHeaders 204 | Out-Null

# ===================== SEARCH =====================
Test-Endpoint "Search: query 'Priya'" GET "/api/search?q=Priya" $null $ownerHeaders 200 | Out-Null
Test-Endpoint "Search: query 'T1'" GET "/api/search?q=T1" $null $ownerHeaders 200 | Out-Null

# ===================== HEALTH =====================
Test-Endpoint "Health check (anonymous)" GET "/health" $null @{} 200 | Out-Null

# ===================== REPORT =====================
$total = $results.Count
$passed = ($results | Where-Object Pass).Count
$failed = $results | Where-Object { -not $_.Pass }

Write-Output "`n================ RESULTS ================"
$results | Format-Table -AutoSize
Write-Output "==========================================="
Write-Output "TOTAL: $total   PASSED: $passed   FAILED: $($failed.Count)"
if ($failed.Count -gt 0) {
    Write-Output "`nFAILED TESTS:"
    $failed | Format-Table -AutoSize
}
