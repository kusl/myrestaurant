using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Playwright;
using MyRestaurant.Domain.Security;
using MyRestaurant.EndToEnd.Tests.Harness;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Displays;
using MyRestaurant.WebApplication.Identity;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.EndToEnd.Tests;

public sealed class EndToEndScenarios : IClassFixture<RestaurantHarness>
{
    private const int BoundaryWatchingRotationSeconds = 20;

    private const int ShortestRotationSeconds = RestaurantOptions.MinimumTableJoinTokenRotationSeconds;

    private const int ImpatientReminderSeconds = 5;

    private const string CounterPassword = "settle up at the till please";

    private const decimal AdjustedPieUnitPrice = 11.00m;

    private const string PriceAdjustmentReason = "Small bowl, agreed at the till";

    private const int AdjustedLineQuantity = 2;

    private const string ClosingCounterPassword = "close the table and cash up";

    private const int UndeliveredLineQuantity = 2;

    private const int HiddenOrderPieQuantity = 3;

    private const int HiddenOrderLineCount = 2;

    private const string FirstKitchenPassword = "hot pass and a clean board";

    private const string ReenrolledKitchenPassword = "a new pass for a new week";

    private static readonly string[] HandheldAdministrationIndexPaths =
    [
        "/administration",
        "/administration/tables",
        "/administration/menu",
        "/administration/sittings",
        "/administration/hidden-records",
        "/administration/events",
    ];

    private static string[] HandheldDetailPaths(Guid personIdentifier, Guid tableIdentifier, Guid menuItemIdentifier)
        =>
        [
            string.Create(CultureInfo.InvariantCulture, $"/administration/people/{personIdentifier:D}"),
            string.Create(CultureInfo.InvariantCulture, $"/administration/tables/{tableIdentifier:D}"),
            string.Create(CultureInfo.InvariantCulture, $"/administration/tables/{tableIdentifier:D}/displays"),
            string.Create(CultureInfo.InvariantCulture, $"/administration/menu/{menuItemIdentifier:D}"),
        ];

    private const double ScrollbarAllowancePixels = 20.0;

    private const int MinimumControlsMeasured = 14;

    private const string HandheldCounterUsername = "e2e.sixteen.counter";

    private const string HandheldCounterDisplayName = "Anastasia Featherstonehaughwolstenholmeworthington";

    private const string HandheldTableLabel = "E2E Sixteen";

    private const string HandheldMenuItemName = "Handheld Soup";

    private const decimal HandheldMenuItemPrice = 6.50m;

    private readonly RestaurantHarness _harness;

    public EndToEndScenarios(RestaurantHarness harness) => _harness = harness;

    [Fact]
    public async Task Setup_BootstrapsFirstAdministratorThenBecomes404()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);
        IPage page = instance.Page;

        IResponse? open = await page.GotoAsync(AccountRoutes.Setup);
        Assert.NotNull(open);
        Assert.Equal(200, open.Status);
        Assert.Equal("Create the first administrator", await HeadingAsync(page));

        IReadOnlyList<string> recoveryCodes =
            await AccountJourneys.CompleteSetupAsync(page, AccountJourneys.DefaultAdministrator);

        Assert.Equal("You are the administrator", await HeadingAsync(page));
        Assert.Equal(AccountJourneys.ExpectedRecoveryCodeCount, recoveryCodes.Count);
        Assert.All(recoveryCodes, code => Assert.False(string.IsNullOrWhiteSpace(code)));

        IResponse? closed = await page.GotoAsync(AccountRoutes.Setup);
        Assert.NotNull(closed);
        Assert.Equal(404, closed.Status);
    }

    [Fact]
    public async Task Display_PairsAndShowsRotatingQrAcrossWindowBoundary()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Two";

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(
                BoundaryWatchingRotationSeconds, cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        string pairingCode = await AdministrationJourneys.IssuePairingCodeAsync(administrator, tableIdentifier);

        IPage display = await instance.OpenIsolatedPageAsync();

        await display.GotoAsync(DisplayRoutes.ForTable(tableIdentifier));
        Assert.Equal("Pair this display", await HeadingAsync(display));

        Guid pairedTable = await DisplayJourneys.PairAsync(display, pairingCode, "E2E Window Tablet");

        Assert.Equal(tableIdentifier, pairedTable);
        Assert.Equal(tableLabel, await HeadingAsync(display));

        await DisplayJourneys.WaitForLiveSurfaceAsync(display, InteractivityPatience);

        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        string firstCode = await DisplayJourneys.ReadJoinQrPathAsync(display);
        AssertShowingLiveJoinCode(firstCode, joinSecret, tableIdentifier, instance);

        string secondCode = await DisplayJourneys.WaitForJoinQrPathAsync(
            display,
            candidate => !string.Equals(candidate, firstCode, StringComparison.Ordinal),
            RefreshPatience(instance.TableJoinTokenRotationSeconds),
            "a join code different from the one it started on",
            cancellationToken);

        Assert.NotEqual(firstCode, secondCode);
        AssertShowingLiveJoinCode(secondCode, joinSecret, tableIdentifier, instance);
    }

    [Fact]
    public async Task Guest_ScansRegistersWithPasskeyAndJoins()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Three";
        GuestAccount guestAccount = new("e2e.guest", "Hungry Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(
                ShortestRotationSeconds, cancellationToken: cancellationToken);

        int rotationSeconds = instance.TableJoinTokenRotationSeconds;

        byte[] joinSecret = RandomNumberGenerator.GetBytes(32);
        Guid tableIdentifier = await instance.InsertActiveTableAsync(tableLabel, joinSecret, cancellationToken);

        IPage guest = await instance.OpenIsolatedPageAsync(withVirtualAuthenticator: true);

        DateTimeOffset scannedAt = DateTimeOffset.UtcNow;
        string scannedToken = JoinTokenService.ComputeCurrentToken(
            joinSecret, tableIdentifier, scannedAt, rotationSeconds);

        TableJourneys.JoinStage afterScan =
            await TableJourneys.ScanAsync(guest, tableIdentifier, scannedToken);

        Assert.Equal(TableJourneys.JoinStage.SentToSignIn, afterScan);
        Assert.Contains(tableIdentifier.ToString("D"), guest.Url, StringComparison.Ordinal);

        await AccountJourneys.RegisterGuestWithPasskeyAsync(guest, guestAccount);

        Assert.DoesNotContain("token=", guest.Url, StringComparison.Ordinal);
        Assert.Equal(TableJourneys.JoinStage.Confirm, await TableJourneys.JoinStageOnScreen(guest));

        await WaitUntilTokenIsDeadAsync(scannedAt, rotationSeconds, cancellationToken);

        IPage bystander = await instance.OpenIsolatedPageAsync();
        Assert.Equal(
            TableJourneys.JoinStage.Expired,
            await TableJourneys.ScanAsync(bystander, tableIdentifier, scannedToken));

        await TableJourneys.JoinAsync(guest);

        Assert.Equal(TableJourneys.JoinStage.Member, await TableJourneys.JoinStageOnScreen(guest));
        Assert.Equal(tableLabel, await HeadingAsync(guest));

        OpenSitting? sitting = await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken);

        Assert.NotNull(sitting);
        Assert.Equal(guestAccount.Username, Assert.Single(sitting!.MemberUsernames));
    }

    [Fact]
    public async Task PasskeySignIn_OfTotpUser_SkipsTotpChallenge()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);
        IPage page = instance.Page;
        AdministratorAccount account = AccountJourneys.DefaultAdministrator;

        await AccountJourneys.CompleteSetupAsync(page, account);
        await AccountJourneys.SignOutAsync(page);
        await AccountJourneys.SignInWithPasskeyAsync(page, account.Username);

        Assert.DoesNotContain(AccountRoutes.SignInTwoFactor, page.Url);
        Assert.DoesNotContain(AccountRoutes.SignInRecoveryCode, page.Url);

        ILocator sessionName = page.Locator("span.session-name").First;
        await sessionName.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        Assert.Equal(account.Username, (await sessionName.InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task JoinToken_ExpiredShowsFriendlyPage_PreviousWindowAccepted()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const int rotationSeconds = RestaurantInstance.DefaultTableJoinTokenRotationSeconds;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(rotationSeconds, cancellationToken: cancellationToken);
        IPage page = instance.Page;

        byte[] joinSecret = RandomNumberGenerator.GetBytes(32);
        Guid tableIdentifier = await instance.InsertActiveTableAsync(
            "E2E Fourteen", joinSecret, cancellationToken);

        long currentWindow = JoinTokenService.CurrentWindowIndex(DateTimeOffset.UtcNow, rotationSeconds);

        string staleToken = JoinTokenService.ComputeToken(joinSecret, tableIdentifier, currentWindow - 4);
        await page.GotoAsync(JoinPath(tableIdentifier, staleToken));

        Assert.Equal("That code has expired", await HeadingAsync(page));
        Assert.DoesNotContain(AccountRoutes.SignIn, page.Url);

        string previousWindowToken =
            JoinTokenService.ComputeToken(joinSecret, tableIdentifier, currentWindow - 1);
        await page.GotoAsync(JoinPath(tableIdentifier, previousWindowToken));

        Assert.Contains(AccountRoutes.SignIn, page.Url);
        Assert.Contains(tableIdentifier.ToString("D"), page.Url);
    }

    [Fact]
    public async Task Admin_RotatesJoinSecret_InFlightTokenDiesNextWindowWorks()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Fifteen";

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(
                BoundaryWatchingRotationSeconds, cancellationToken: cancellationToken);

        int rotationSeconds = instance.TableJoinTokenRotationSeconds;

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        string pairingCode = await AdministrationJourneys.IssuePairingCodeAsync(administrator, tableIdentifier);

        IPage display = await instance.OpenIsolatedPageAsync();
        await DisplayJourneys.PairAsync(display, pairingCode, "E2E Rotation Tablet");

        await DisplayJourneys.WaitForLiveSurfaceAsync(display, InteractivityPatience);

        byte[] originalSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);
        string codeBeforeRotation = await DisplayJourneys.ReadJoinQrPathAsync(display);
        AssertShowingLiveJoinCode(codeBeforeRotation, originalSecret, tableIdentifier, instance);

        string inFlightToken = JoinTokenService.ComputeCurrentToken(
            originalSecret, tableIdentifier, DateTimeOffset.UtcNow, rotationSeconds);

        await AdministrationJourneys.RotateJoinSecretAsync(administrator, tableIdentifier);

        byte[] rotatedSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);
        Assert.NotEqual(originalSecret, rotatedSecret);

        IPage guest = await instance.OpenIsolatedPageAsync();
        await guest.GotoAsync(JoinPath(tableIdentifier, inFlightToken));

        Assert.Equal("That code has expired", await HeadingAsync(guest));
        Assert.DoesNotContain(AccountRoutes.SignIn, guest.Url);

        string codeAfterRotation = await DisplayJourneys.WaitForJoinQrPathAsync(
            display,
            candidate => JoinQrCodes.IsLive(
                candidate,
                rotatedSecret,
                tableIdentifier,
                instance.PublicOrigin,
                DateTimeOffset.UtcNow,
                rotationSeconds),
            RefreshPatience(rotationSeconds),
            "a join code signed by the rotated secret",
            cancellationToken);

        Assert.NotEqual(codeBeforeRotation, codeAfterRotation);

        string freshToken = JoinTokenService.ComputeCurrentToken(
            rotatedSecret, tableIdentifier, DateTimeOffset.UtcNow, rotationSeconds);
        await guest.GotoAsync(JoinPath(tableIdentifier, freshToken));

        Assert.Contains(AccountRoutes.SignIn, guest.Url);
        Assert.Contains(tableIdentifier.ToString("D"), guest.Url);
    }

    [Fact]
    public async Task Guest_StagesAddsAndSend_KitchenGetsOneAlert()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Four";
        const string customizationNote = "No onions, extra hot";
        GuestAccount guestAccount = new("e2e.guest.four", "Four Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        ArrangedService service = await ArrangeServiceAsync(
            instance, tableLabel, guestAccount, cancellationToken);

        Assert.Contains(service.TableIdentifier.ToString("D"), service.Guest.Url, StringComparison.Ordinal);

        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1, customizationNote);
        await TableOrderJourneys.StageAsync(service.Guest, service.Pie, 2);

        Assert.Equal(2, await TableOrderJourneys.BasketLineCountAsync(service.Guest));

        KitchenBoardSnapshot beforeSend = await KitchenJourneys.ReadBoardAsync(service.Kitchen);
        Assert.Equal(0, beforeSend.UnseenAlertCount);
        Assert.Empty(beforeSend.PendingLines);

        string confirmation = await TableOrderJourneys.SendAsync(service.Guest);

        Assert.Contains("2 items", confirmation, StringComparison.Ordinal);

        KitchenBoardSnapshot board = await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 2 && snapshot.UnseenAlertCount >= 1,
            LiveUpdatePatience,
            "two lines on the pass and at least one unacknowledged alert",
            cancellationToken);

        Assert.Equal(1, board.UnseenAlertCount);

        KitchenBoardLine soupOnThePass =
            Assert.Single(board.PendingLines, line => line.Name == service.Soup.Name);
        KitchenBoardLine pieOnThePass =
            Assert.Single(board.PendingLines, line => line.Name == service.Pie.Name);

        Assert.Equal(1, soupOnThePass.Quantity);
        Assert.Equal(customizationNote, soupOnThePass.Note);
        Assert.Equal(2, pieOnThePass.Quantity);
        Assert.Null(pieOnThePass.Note);

        IReadOnlyList<GuestOrderLine> guestLines = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(guestLines, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));
        Assert.Equal(0, await TableOrderJourneys.BasketLineCountAsync(service.Guest));

        KitchenBoardSnapshot settled = await KitchenJourneys.ReadBoardAsync(service.Kitchen);

        Assert.Equal(1, settled.UnseenAlertCount);
    }

    [Fact]
    public async Task SecondGuest_JoinsAndSeesOrderLiveWithRosterUpdate()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Five";
        GuestAccount firstAccount = new("e2e.guest.five.one", "First Guest");
        GuestAccount secondAccount = new("e2e.guest.five.two", "Second Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        IPage first = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, firstAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(first, soup, 1);
        await TableOrderJourneys.SendAsync(first);

        await TableOrderJourneys.WaitForCommittedLinesAsync(
            first,
            lines => lines.Count == 1,
            LiveUpdatePatience,
            "the soup on the first guest's own order",
            cancellationToken);

        TableRosterMember aloneAtTheTable =
            Assert.Single(await TableOrderJourneys.ReadRosterAsync(first));

        Assert.Equal(firstAccount.DisplayName, aloneAtTheTable.Name);
        Assert.True(aloneAtTheTable.IsYou);

        Assert.Empty(await TableOrderJourneys.ReadPartyAsync(first));

        IPage second = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, secondAccount, cancellationToken);

        IReadOnlyList<TableRosterMember> roster = await TableOrderJourneys.WaitForRosterAsync(
            first,
            members => members.Count == 2,
            LiveUpdatePatience,
            "both guests on the roster",
            cancellationToken);

        Assert.Equal(firstAccount.DisplayName, Assert.Single(roster, member => member.IsYou).Name);
        Assert.Equal(secondAccount.DisplayName, Assert.Single(roster, member => !member.IsYou).Name);

        Assert.Empty(await TableOrderJourneys.ReadPartyAsync(first));

        IReadOnlyList<PartyOrder> onArrival = await TableOrderJourneys.WaitForPartyAsync(
            second,
            party => party.Count == 1 && party[0].Lines.Count == 1,
            LiveUpdatePatience,
            "the first guest's soup under the rest of the table",
            cancellationToken);

        PartyOrder theirOrderOnArrival = Assert.Single(onArrival);

        Assert.Equal(firstAccount.DisplayName, theirOrderOnArrival.BillName);
        Assert.Contains(
            soup.Name,
            Assert.Single(theirOrderOnArrival.Lines).Name,
            StringComparison.Ordinal);
        Assert.Equal(GuestLineBadge.WithTheKitchen, theirOrderOnArrival.Lines[0].Badge);

        await TableOrderJourneys.StageAsync(first, pie, 2);
        await TableOrderJourneys.SendAsync(first);

        IReadOnlyList<PartyOrder> afterSecondSend = await TableOrderJourneys.WaitForPartyAsync(
            second,
            party => party.Count == 1 && party[0].Lines.Count == 2,
            LiveUpdatePatience,
            "both of the first guest's lines under the rest of the table",
            cancellationToken);

        PartyOrder theirGrownOrder = Assert.Single(afterSecondSend);

        Assert.Equal(firstAccount.DisplayName, theirGrownOrder.BillName);
        Assert.Single(theirGrownOrder.Lines, line => line.Name.Contains(soup.Name, StringComparison.Ordinal));

        GuestOrderLine pieOnTheirOrder = Assert.Single(
            theirGrownOrder.Lines,
            line => line.Name.Contains(pie.Name, StringComparison.Ordinal));

        Assert.StartsWith("2 ", pieOnTheirOrder.Name, StringComparison.Ordinal);

        OpenSitting? sitting = await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken);

        Assert.NotNull(sitting);
        Assert.Equal(2, sitting!.MemberUsernames.Count);
        Assert.Equal(firstAccount.Username, sitting.MemberUsernames[0]);
        Assert.Equal(secondAccount.Username, sitting.MemberUsernames[1]);
    }

    [Fact]
    public async Task Kitchen_FulfillsLine_GuestSeesFulfilledBadge()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Six";
        GuestAccount guestAccount = new("e2e.guest.six", "Six Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        ArrangedService service = await ArrangeServiceAsync(
            instance, tableLabel, guestAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1);
        await TableOrderJourneys.StageAsync(service.Guest, service.Pie, 1);
        await TableOrderJourneys.SendAsync(service.Guest);

        await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 2,
            LiveUpdatePatience,
            "both of the guest's lines on the pass",
            cancellationToken);

        IReadOnlyList<GuestOrderLine> beforeFulfillment = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(beforeFulfillment, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));

        await KitchenJourneys.FulfillLineAsync(service.Kitchen, service.Soup.Name);

        IReadOnlyList<GuestOrderLine> afterFulfillment = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Any(line => line.Badge == GuestLineBadge.AtYourTable),
            LiveUpdatePatience,
            "a line badged as at the table",
            cancellationToken);

        GuestOrderLine soupLine = Assert.Single(afterFulfillment,
            line => line.Name.Contains(service.Soup.Name, StringComparison.Ordinal));
        GuestOrderLine pieLine = Assert.Single(afterFulfillment,
            line => line.Name.Contains(service.Pie.Name, StringComparison.Ordinal));

        Assert.Equal(GuestLineBadge.AtYourTable, soupLine.Badge);
        Assert.Equal(GuestLineBadge.WithTheKitchen, pieLine.Badge);

        KitchenBoardSnapshot board = await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 1,
            LiveUpdatePatience,
            "one line left on the pass",
            cancellationToken);

        Assert.Equal(service.Pie.Name, Assert.Single(board.PendingLines).Name);
    }

    [Fact]
    public async Task Guest_RemoveFulfilledLineRejected_RemovePendingSucceeds()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Seven";
        GuestAccount guestAccount = new("e2e.guest.seven", "Seven Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        ArrangedService service = await ArrangeServiceAsync(
            instance, tableLabel, guestAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1);
        await TableOrderJourneys.StageAsync(service.Guest, service.Pie, 1);
        await TableOrderJourneys.SendAsync(service.Guest);

        await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 2,
            LiveUpdatePatience,
            "both of the guest's lines on the pass",
            cancellationToken);

        IReadOnlyList<GuestOrderLine> sent = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(sent, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));

        Assert.True(await TableOrderJourneys.LineOffersRemovalAsync(service.Guest, service.Soup.Name));

        await TableOrderJourneys.MarkForRemovalAsync(service.Guest, service.Soup.Name);

        Assert.Equal(1, await TableOrderJourneys.BasketRemovalCountAsync(service.Guest));

        await KitchenJourneys.FulfillLineAsync(service.Kitchen, service.Soup.Name);

        await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Any(line =>
                line.Name.Contains(service.Soup.Name, StringComparison.Ordinal)
                && line.Badge == GuestLineBadge.AtYourTable),
            LiveUpdatePatience,
            "the soup badged as at the table",
            cancellationToken);

        await TableOrderJourneys.WaitForBasketAsync(
            service.Guest,
            basket => basket is { StagedAdds: 0, TickedRemovals: 0 },
            LiveUpdatePatience,
            "the stale removal unticked",
            cancellationToken);

        string? unticked = await TableOrderJourneys.ReadPruneNoticeAsync(service.Guest);

        Assert.NotNull(unticked);
        Assert.Contains("no longer yours to remove", unticked, StringComparison.Ordinal);

        Assert.False(await TableOrderJourneys.LineOffersRemovalAsync(service.Guest, service.Soup.Name));
        Assert.True(await TableOrderJourneys.LineOffersRemovalAsync(service.Guest, service.Pie.Name));

        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1);

        await KitchenJourneys.EightySixAsync(service.Kitchen, service.Soup.Name);

        await TableOrderJourneys.WaitForBasketAsync(
            service.Guest,
            basket => basket is { StagedAdds: 1, UnavailableMarks: 1 },
            LiveUpdatePatience,
            "the staged soup marked unavailable",
            cancellationToken);

        await TableOrderJourneys.MarkForRemovalAsync(service.Guest, service.Pie.Name);

        IReadOnlyList<string> refusal =
            await TableOrderJourneys.SendExpectingRefusalAsync(service.Guest);

        string only = Assert.Single(refusal);

        Assert.Contains(service.Soup.Name, only, StringComparison.Ordinal);
        Assert.Contains("currently unavailable", only, StringComparison.Ordinal);

        await TableOrderJourneys.WaitForBasketAsync(
            service.Guest,
            basket => basket is { StagedAdds: 1, TickedRemovals: 1 },
            LiveUpdatePatience,
            "the basket exactly as the guest left it",
            cancellationToken);

        IReadOnlyList<GuestOrderLine> untouched =
            await TableOrderJourneys.ReadCommittedLinesAsync(service.Guest);

        Assert.Equal(2, untouched.Count);
        Assert.Equal(
            GuestLineBadge.WithTheKitchen,
            Assert.Single(untouched, line => line.Name.Contains(service.Pie.Name, StringComparison.Ordinal))
                .Badge);

        KitchenBoardSnapshot stillWaiting = await KitchenJourneys.ReadBoardAsync(service.Kitchen);

        Assert.Equal(service.Pie.Name, Assert.Single(stillWaiting.PendingLines).Name);

        await TableOrderJourneys.UnstageAsync(service.Guest, service.Soup.Name);

        string confirmation = await TableOrderJourneys.SendAsync(service.Guest);

        Assert.Contains("taken off", confirmation, StringComparison.Ordinal);

        IReadOnlyList<GuestOrderLine> afterRemoval = await TableOrderJourneys.WaitForCommittedLinesAsync(
            service.Guest,
            lines => lines.Any(line =>
                line.Name.Contains(service.Pie.Name, StringComparison.Ordinal)
                && line.Badge == GuestLineBadge.Removed),
            LiveUpdatePatience,
            "the pie struck through as removed",
            cancellationToken);

        Assert.Equal(2, afterRemoval.Count);
        Assert.Equal(
            GuestLineBadge.AtYourTable,
            Assert.Single(afterRemoval, line => line.Name.Contains(service.Soup.Name, StringComparison.Ordinal))
                .Badge);

        await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 0,
            LiveUpdatePatience,
            "nothing left on the pass",
            cancellationToken);
    }

    [Fact]
    public async Task Send_UnfulfilledPastThreshold_YieldsExactlyOneReminder()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Eight";
        GuestAccount guestAccount = new("e2e.guest.eight", "Eight Guest");

        await using RestaurantInstance instance = await _harness.StartInstanceAsync(
            kitchenSubmissionReminderSeconds: ImpatientReminderSeconds,
            cancellationToken: cancellationToken);

        ArrangedService service = await ArrangeServiceAsync(
            instance, tableLabel, guestAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(service.Guest, service.Soup, 1);
        await TableOrderJourneys.SendAsync(service.Guest);

        KitchenBoardSnapshot afterSend = await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.PendingLines.Count == 1 && snapshot.UnseenAlertCount == 1,
            LiveUpdatePatience,
            "the sent line on the pass under §10.1's single initial alert",
            cancellationToken);

        Assert.Equal(0, afterSend.UnseenReminderCount);
        Assert.Equal(service.Soup.Name, Assert.Single(afterSend.PendingLines).Name);

        OpenSitting? sitting =
            await instance.ReadOpenSittingAsync(service.TableIdentifier, cancellationToken);

        Assert.NotNull(sitting);

        Assert.Equal(
            new KitchenNotificationTally(Initial: 1, Reminder: 0),
            await instance.ReadKitchenNotificationsAsync(sitting!.SittingIdentifier, cancellationToken));

        KitchenBoardSnapshot reminded = await KitchenJourneys.WaitForBoardAsync(
            service.Kitchen,
            snapshot => snapshot.UnseenReminderCount >= 1,
            ReminderPatience(instance.KitchenSubmissionReminderSeconds),
            "the overdue send's reminder counted on the badge",
            cancellationToken);

        Assert.Equal(1, reminded.UnseenReminderCount);
        Assert.Equal(2, reminded.UnseenAlertCount);

        Assert.Equal(service.Soup.Name, Assert.Single(reminded.PendingLines).Name);

        Assert.Equal(
            new KitchenNotificationTally(Initial: 1, Reminder: 1),
            await instance.ReadKitchenNotificationsAsync(sitting.SittingIdentifier, cancellationToken));

        await KitchenJourneys.AcknowledgeAlertsAsync(service.Kitchen);

        KitchenBoardSnapshot quiet = await KitchenJourneys.WatchBoardAsync(
            service.Kitchen, QuietWatch, cancellationToken);

        Assert.Equal(0, quiet.UnseenReminderCount);
        Assert.Equal(0, quiet.UnseenAlertCount);
        Assert.Equal(service.Soup.Name, Assert.Single(quiet.PendingLines).Name);

        Assert.Equal(
            new KitchenNotificationTally(Initial: 1, Reminder: 1),
            await instance.ReadKitchenNotificationsAsync(sitting.SittingIdentifier, cancellationToken));
    }

    [Fact]
    public async Task Counter_AdjustsPriceWithReason_GuestSeesOldToNew()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Nine";
        GuestAccount guestAccount = new("e2e.guest.nine", "Nine Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        StaffAccount counterAccount = await AdministrationJourneys.CreateStaffAccountAsync(
            administrator, "e2e.counter.nine", "Nine Counter", StaffRoles.Counter);

        Assert.NotEqual(string.Empty, counterAccount.TemporaryPassword);

        decimal unadjustedTableTotal = soup.PriceAmount + (AdjustedLineQuantity * pie.PriceAmount);
        decimal adjustedTableTotal = soup.PriceAmount + (AdjustedLineQuantity * AdjustedPieUnitPrice);

        IPage guest = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(guest, soup, 1);
        await TableOrderJourneys.StageAsync(guest, pie, AdjustedLineQuantity);
        await TableOrderJourneys.SendAsync(guest);

        IReadOnlyList<GuestOrderLine> sent = await TableOrderJourneys.WaitForCommittedLinesAsync(
            guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(sent, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));

        GuestOrderLineDetail beforeAdjustment = await TableOrderJourneys.WaitForOwnLineAsync(
            guest,
            pie.Name,
            line => line.PriceAdjustments.Count == 0,
            LiveUpdatePatience,
            "the pie at its unadjusted menu price",
            cancellationToken);

        Assert.Equal(Money(pie.PriceAmount * AdjustedLineQuantity), beforeAdjustment.PriceText);

        IPage counter = await instance.OpenIsolatedPageAsync();

        await AccountJourneys.SignInWithPasswordAsync(
            counter, counterAccount.Username, counterAccount.TemporaryPassword);

        Assert.Contains(
            AccountRoutes.ForcedPasswordChange, counter.Url, StringComparison.Ordinal);

        await AccountJourneys.CompleteForcedPasswordChangeAsync(
            counter, counterAccount.TemporaryPassword, CounterPassword);

        Guid openedSitting = await CounterJourneys.OpenSittingAsync(
            counter, tableLabel, InteractivityPatience);

        OpenSitting? sitting = await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken);

        Assert.NotNull(sitting);
        Assert.Equal(sitting!.SittingIdentifier, openedSitting);

        CounterBill onArrival = await CounterJourneys.ReadBillAsync(counter);

        Assert.Equal(tableLabel, onArrival.TableLabel);
        Assert.Equal(Money(unadjustedTableTotal), onArrival.RunningTotalText);

        CounterBillEntry theirBill = Assert.Single(onArrival.People);

        Assert.Equal(guestAccount.DisplayName, theirBill.BillName);
        Assert.Equal(Money(unadjustedTableTotal), theirBill.PersonTotalText);

        CounterBillLine pieAtTheTill = Assert.Single(theirBill.Lines, line => line.Name == pie.Name);

        Assert.Equal(AdjustedLineQuantity, pieAtTheTill.Quantity);
        Assert.Equal(Money(pie.PriceAmount), pieAtTheTill.UnitPriceText);
        Assert.False(pieAtTheTill.IsDelivered);

        await CounterJourneys.AdjustPriceAsync(
            counter, pie.Name, AdjustedPieUnitPrice, PriceAdjustmentReason);

        GuestOrderLineDetail adjusted = await TableOrderJourneys.WaitForOwnLineAsync(
            guest,
            pie.Name,
            line => line.PriceAdjustments.Count == 1,
            LiveUpdatePatience,
            "the counter's price adjustment written under the pie",
            cancellationToken);

        GuestPriceAdjustment shown = Assert.Single(adjusted.PriceAdjustments);

        Assert.Equal(Money(pie.PriceAmount), shown.PreviousPriceText);
        Assert.Equal(Money(AdjustedPieUnitPrice), shown.NewPriceText);

        Assert.Contains(PriceAdjustmentReason, shown.Sentence, StringComparison.Ordinal);
        Assert.Contains("the counter", shown.Sentence, StringComparison.Ordinal);

        Assert.Equal(Money(AdjustedPieUnitPrice * AdjustedLineQuantity), adjusted.PriceText);

        Assert.Equal(GuestLineBadge.WithTheKitchen, adjusted.Badge);

        GuestOrderLineDetail untouched = await TableOrderJourneys.WaitForOwnLineAsync(
            guest,
            soup.Name,
            line => line.PriceAdjustments.Count == 0,
            LiveUpdatePatience,
            "the soup with no adjustment against it",
            cancellationToken);

        Assert.Equal(Money(soup.PriceAmount), untouched.PriceText);
        Assert.Equal(GuestLineBadge.WithTheKitchen, untouched.Badge);

        CounterBill afterAdjustment = await CounterJourneys.ReadBillAsync(counter);

        Assert.Equal(Money(adjustedTableTotal), afterAdjustment.RunningTotalText);

        CounterBillEntry rebilled = Assert.Single(afterAdjustment.People);

        Assert.Equal(Money(adjustedTableTotal), rebilled.PersonTotalText);

        CounterBillLine pieRebilled = Assert.Single(rebilled.Lines, line => line.Name == pie.Name);

        Assert.Equal(Money(AdjustedPieUnitPrice), pieRebilled.UnitPriceText);
        Assert.Equal(Money(AdjustedPieUnitPrice * AdjustedLineQuantity), pieRebilled.LineTotalText);

        CounterBillLine soupRebilled = Assert.Single(rebilled.Lines, line => line.Name == soup.Name);

        Assert.Equal(Money(soup.PriceAmount), soupRebilled.UnitPriceText);
    }

    [Fact]
    public async Task Counter_ClosesSittingFromAHandheld_TableFlipsToSettledAndTotalsMatch()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Ten";
        GuestAccount guestAccount = new("e2e.guest.ten", "Ten Guest");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        StaffAccount counterAccount = await AdministrationJourneys.CreateStaffAccountAsync(
            administrator, "e2e.counter.ten", "Ten Counter", StaffRoles.Counter);

        decimal tableTotal = soup.PriceAmount + (UndeliveredLineQuantity * pie.PriceAmount);
        string expectedTotal = Money(tableTotal);

        IPage guest = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, cancellationToken);

        await TableOrderJourneys.StageAsync(guest, soup, 1);
        await TableOrderJourneys.StageAsync(guest, pie, UndeliveredLineQuantity);
        await TableOrderJourneys.SendAsync(guest);

        IReadOnlyList<GuestOrderLine> sent = await TableOrderJourneys.WaitForCommittedLinesAsync(
            guest,
            lines => lines.Count == 2,
            LiveUpdatePatience,
            "both sent lines on the guest's own order",
            cancellationToken);

        Assert.All(sent, line => Assert.Equal(GuestLineBadge.WithTheKitchen, line.Badge));

        await KitchenJourneys.OpenAsync(administrator, InteractivityPatience);

        await KitchenJourneys.WaitForBoardAsync(
            administrator,
            board => board.PendingLines.Count == 2,
            LiveUpdatePatience,
            "both of the guest's lines waiting on the pass",
            cancellationToken);

        await KitchenJourneys.FulfillLineAsync(administrator, soup.Name);

        await TableOrderJourneys.WaitForOwnLineAsync(
            guest,
            soup.Name,
            line => line.Badge == GuestLineBadge.AtYourTable,
            LiveUpdatePatience,
            "the soup re-badged as delivered",
            cancellationToken);

        IPage counter = await instance.OpenIsolatedPageAsync(handheld: true);

        await AccountJourneys.SignInWithPasswordAsync(
            counter, counterAccount.Username, counterAccount.TemporaryPassword);

        Assert.Contains(AccountRoutes.ForcedPasswordChange, counter.Url, StringComparison.Ordinal);

        await AccountJourneys.CompleteForcedPasswordChangeAsync(
            counter, counterAccount.TemporaryPassword, ClosingCounterPassword);

        AssertHandheldBarrier(
            await HandheldReach.MeasureAsync(
                counter, CounterJourneys.BoardPath, HandheldSurface.CounterBoard),
            "§11.3's counter board");

        Guid sittingIdentifier = await CounterJourneys.OpenSittingAsync(
            counter, tableLabel, InteractivityPatience);

        OpenSitting? openSitting =
            await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken);

        Assert.NotNull(openSitting);
        Assert.Equal(openSitting!.SittingIdentifier, sittingIdentifier);

        CounterBill beforeClose = await CounterJourneys.ReadBillAsync(counter);

        Assert.Equal(tableLabel, beforeClose.TableLabel);
        Assert.Equal(expectedTotal, beforeClose.RunningTotalText);

        CounterBillEntry theirBill = Assert.Single(beforeClose.People);

        Assert.Equal(guestAccount.DisplayName, theirBill.BillName);
        Assert.Equal(expectedTotal, theirBill.PersonTotalText);

        CounterBillLine soupAtTheTill = Assert.Single(theirBill.Lines, line => line.Name == soup.Name);
        CounterBillLine pieAtTheTill = Assert.Single(theirBill.Lines, line => line.Name == pie.Name);

        Assert.True(soupAtTheTill.IsDelivered);
        Assert.False(pieAtTheTill.IsDelivered);
        Assert.Equal(UndeliveredLineQuantity, pieAtTheTill.Quantity);

        CounterPendingWarning? warning = await CounterJourneys.ReadPendingWarningAsync(counter);

        Assert.NotNull(warning);

        Assert.Equal(1, warning!.LineCount);
        Assert.Contains("still with the kitchen", warning.Sentence, StringComparison.Ordinal);

        AssertHandheldBarrier(
            await HandheldReach.MeasureHereAsync(
                counter, counter.Url, HandheldSurface.CounterBill),
            "§11.3's bill at the till");

        CloseConfirmation confirmation = await CounterJourneys.BeginCloseAsync(counter);

        Assert.Equal(expectedTotal, confirmation.AmountText);
        Assert.Contains(tableLabel, confirmation.Sentence, StringComparison.Ordinal);

        SettledTill settled = await CounterJourneys.ConfirmCloseAsync(counter, InteractivityPatience);

        Assert.True(
            settled.SaysReadOnly,
            "§11.3 must say a settled sitting is settled: " + CounterJourneys.DescribeSettled(settled));

        Assert.Equal("Settled total", settled.TotalLabel);
        Assert.Equal(expectedTotal, settled.TotalText);
        Assert.Equal(expectedTotal, settled.TableTotalText);

        Assert.False(
            settled.ShowsCorrection,
            "no §6.7 correction has been made, so no corrected total should be shown: "
                + CounterJourneys.DescribeSettled(settled));

        Assert.Equal(0, settled.LineControlCount);
        Assert.False(settled.OffersStaffAdd);
        Assert.False(settled.OffersClose);

        Assert.Contains(counterAccount.DisplayName, settled.HeaderMeta, StringComparison.Ordinal);

        Assert.NotNull(settled.Notice);
        Assert.Contains(expectedTotal, settled.Notice!, StringComparison.Ordinal);
        Assert.Contains("still with the kitchen", settled.Notice, StringComparison.Ordinal);

        CounterBill afterClose = await CounterJourneys.ReadBillAsync(counter);
        CounterBillEntry rebilled = Assert.Single(afterClose.People);
        CounterBillLine pieAfterClose = Assert.Single(rebilled.Lines, line => line.Name == pie.Name);

        Assert.False(pieAfterClose.IsDelivered);

        GuestSettledView guestView = await TableOrderJourneys.WaitForSettledViewAsync(
            guest, LiveUpdatePatience, cancellationToken);

        Assert.False(
            guestView.OffersPicker,
            "a settled sitting must offer the guest nothing to order: "
                + TableOrderJourneys.DescribeSettledView(guestView));

        Assert.False(guestView.OffersSend);
        Assert.Equal(0, guestView.RemovalCheckboxes);

        Assert.Equal(expectedTotal, guestView.Totals.TableTotalText);

        Assert.Equal(expectedTotal, guestView.Totals.YourTotalText);

        Assert.Equal(2, guestView.Lines.Count);

        GuestOrderLineDetail soupOnTheBill =
            Assert.Single(guestView.Lines, line => line.Name.Contains(soup.Name, StringComparison.Ordinal));
        GuestOrderLineDetail pieOnTheBill =
            Assert.Single(guestView.Lines, line => line.Name.Contains(pie.Name, StringComparison.Ordinal));

        Assert.Equal(GuestLineBadge.AtYourTable, soupOnTheBill.Badge);
        Assert.Equal(GuestLineBadge.WithTheKitchen, pieOnTheBill.Badge);
        Assert.Equal(Money(pie.PriceAmount * UndeliveredLineQuantity), pieOnTheBill.PriceText);

        SettledSitting? row =
            await instance.ReadSettledSittingAsync(sittingIdentifier, cancellationToken);

        Assert.NotNull(row);
        Assert.Equal(tableTotal, row!.SettledTotalAmount);
        Assert.Equal(counterAccount.Username, row.ClosedByUsername);

        Assert.Null(await instance.ReadOpenSittingAsync(tableIdentifier, cancellationToken));

        await counter.GotoAsync(CounterJourneys.BoardPath);
        await CounterJourneys.WaitForBoardAsync(counter, InteractivityPatience);

        CounterFloor floor = await CounterJourneys.ReadFloorAsync(counter);

        Assert.DoesNotContain(tableLabel, floor.OpenTableLabels);

        SettledTableRow settledRow = Assert.Single(
            floor.Settled, candidate => candidate.TableLabel == tableLabel);

        Assert.Equal(expectedTotal, settledRow.AmountText);
        Assert.Contains(counterAccount.DisplayName, settledRow.SettledBy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guest_HidesClosedOrder_AdminCanUnhide()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Eleven";

        GuestAccount hider = new("e2e.guest.eleven.alpha", "Eleven Alpha");
        GuestAccount bystander = new("e2e.guest.eleven.bravo", "Eleven Bravo");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        decimal hiderTotal = soup.PriceAmount + (HiddenOrderPieQuantity * pie.PriceAmount);
        decimal bystanderTotal = soup.PriceAmount;

        string expectedHiderTotal = Money(hiderTotal);
        string expectedBystanderTotal = Money(bystanderTotal);
        string expectedTableTotal = Money(hiderTotal + bystanderTotal);

        IPage alpha = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, hider, cancellationToken);

        await TableOrderJourneys.StageAsync(alpha, soup, 1);
        await TableOrderJourneys.StageAsync(alpha, pie, HiddenOrderPieQuantity);
        await TableOrderJourneys.SendAsync(alpha);

        await TableOrderJourneys.WaitForCommittedLinesAsync(
            alpha,
            lines => lines.Count == HiddenOrderLineCount,
            LiveUpdatePatience,
            "both of the hider's sent lines",
            cancellationToken);

        IPage bravo = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, bystander, cancellationToken);

        await TableOrderJourneys.StageAsync(bravo, soup, 1);
        await TableOrderJourneys.SendAsync(bravo);

        await TableOrderJourneys.WaitForCommittedLinesAsync(
            bravo,
            lines => lines.Count == 1,
            LiveUpdatePatience,
            "the bystander's one sent line",
            cancellationToken);

        Guid sittingIdentifier =
            await CounterJourneys.OpenSittingAsync(administrator, tableLabel, InteractivityPatience);

        CounterBill beforeClose = await CounterJourneys.ReadBillAsync(administrator);

        Assert.Equal(2, beforeClose.People.Count);
        Assert.Equal(expectedTableTotal, beforeClose.RunningTotalText);

        await CounterJourneys.BeginCloseAsync(administrator);

        SettledTill settled = await CounterJourneys.ConfirmCloseAsync(administrator, InteractivityPatience);

        Assert.Equal("Settled total", settled.TotalLabel);
        Assert.Equal(expectedTableTotal, settled.TotalText);

        await HistoryJourneys.OpenAsync(alpha, InteractivityPatience);

        GuestHistory alphaBefore = await HistoryJourneys.ReadAsync(alpha);
        HistoryOrder theirs = Assert.Single(alphaBefore.Orders);

        Assert.Equal(tableLabel, theirs.TableLabel);
        Assert.Equal(expectedHiderTotal, theirs.PersonTotalText);
        Assert.Equal(HiddenOrderLineCount, theirs.LineCount);

        HistoryLine pieInHistory =
            Assert.Single(theirs.Lines, line => line.Name == pie.Name);

        Assert.Equal(HiddenOrderPieQuantity, pieInHistory.Quantity);
        Assert.Equal(Money(pie.PriceAmount * HiddenOrderPieQuantity), pieInHistory.LineTotalText);

        await HistoryJourneys.OpenAsync(bravo, InteractivityPatience);

        GuestHistory bravoBefore = await HistoryJourneys.ReadAsync(bravo);
        HistoryOrder bystanderOrder = Assert.Single(bravoBefore.Orders);

        Assert.Equal(expectedBystanderTotal, bystanderOrder.PersonTotalText);
        Assert.NotEqual(theirs.GuestOrderIdentifier, bystanderOrder.GuestOrderIdentifier);

        await HistoryJourneys.HideAsync(alpha, theirs.GuestOrderIdentifier, InteractivityPatience);

        GuestHistory alphaAfter = await HistoryJourneys.ReadAsync(alpha);

        Assert.Empty(alphaAfter.Orders);

        Assert.NotNull(alphaAfter.EmptySentence);
        Assert.Contains("Nothing here yet", alphaAfter.EmptySentence!, StringComparison.Ordinal);

        Assert.NotNull(alphaAfter.Notice);
        Assert.Contains("a manager can restore it", alphaAfter.Notice!, StringComparison.Ordinal);

        await HistoryJourneys.OpenAsync(bravo, InteractivityPatience);

        GuestHistory bravoAfter = await HistoryJourneys.ReadAsync(bravo);
        HistoryOrder untouched = Assert.Single(bravoAfter.Orders);

        Assert.Equal(bystanderOrder.GuestOrderIdentifier, untouched.GuestOrderIdentifier);
        Assert.Equal(expectedBystanderTotal, untouched.PersonTotalText);

        await CounterJourneys.OpenSettledSittingAsync(
            administrator, sittingIdentifier, InteractivityPatience);

        CounterBill afterHide = await CounterJourneys.ReadBillAsync(administrator);

        Assert.Equal(2, afterHide.People.Count);
        Assert.Equal(expectedTableTotal, afterHide.RunningTotalText);

        CounterBillEntry hidersBill =
            Assert.Single(afterHide.People, entry => entry.BillName == hider.DisplayName);

        Assert.Equal(expectedHiderTotal, hidersBill.PersonTotalText);
        Assert.Equal(HiddenOrderLineCount, hidersBill.Lines.Count);

        SettledSitting? row =
            await instance.ReadSettledSittingAsync(sittingIdentifier, cancellationToken);

        Assert.NotNull(row);
        Assert.Equal(hiderTotal + bystanderTotal, row!.SettledTotalAmount);

        await HiddenRecordJourneys.OpenAsync(administrator, InteractivityPatience);

        HiddenRecordList everything = await HiddenRecordJourneys.ReadAsync(administrator);

        Assert.False(
            everything.IsNarrowed,
            "§11.4's view must open unfiltered: " + HiddenRecordJourneys.Describe(everything));

        HiddenRecordRow found = Assert.Single(everything.Rows);

        Assert.Equal(theirs.GuestOrderIdentifier, found.GuestOrderIdentifier);
        Assert.Equal(sittingIdentifier, found.SittingIdentifier);
        Assert.Equal(hider.Username, found.Username);
        Assert.Equal(hider.DisplayName, found.OwnerName);
        Assert.Equal(expectedHiderTotal, found.PersonTotalText);

        await HiddenRecordJourneys.FilterByUsernameAsync(
            administrator, bystander.Username, InteractivityPatience);

        HiddenRecordList wrongOwner = await HiddenRecordJourneys.ReadAsync(administrator);

        Assert.Empty(wrongOwner.Rows);
        Assert.True(
            wrongOwner.IsNarrowed,
            "the list must know it is filtered: " + HiddenRecordJourneys.Describe(wrongOwner));

        Assert.NotNull(wrongOwner.EmptySentence);
        Assert.Contains("matches that", wrongOwner.EmptySentence!, StringComparison.Ordinal);

        await HiddenRecordJourneys.FilterByUsernameAsync(
            administrator, hider.Username, InteractivityPatience);

        HiddenRecordList rightOwner = await HiddenRecordJourneys.ReadAsync(administrator);
        HiddenRecordRow filtered = Assert.Single(rightOwner.Rows);

        Assert.Equal(theirs.GuestOrderIdentifier, filtered.GuestOrderIdentifier);

        HiddenRecordDetail detail = await HiddenRecordJourneys.ExpandAsync(
            administrator, theirs.GuestOrderIdentifier, InteractivityPatience);

        HiddenVisibilityEntry onlyEvent = Assert.Single(detail.VisibilityLog);

        Assert.Equal("Hidden by the owner", onlyEvent.Description);

        Assert.Contains(hider.DisplayName, onlyEvent.ActorAndTime, StringComparison.Ordinal);

        Assert.True(
            detail.EventCount >= 1,
            "§11.4 must show the order's stored events under a hidden record; the visibility log holds "
                + HiddenRecordJourneys.DescribeVisibilityLog(detail.VisibilityLog));

        Assert.True(detail.OffersUnhide);

        await HiddenRecordJourneys.UnhideAsync(administrator, InteractivityPatience);

        HiddenRecordList afterUnhide = await HiddenRecordJourneys.ReadAsync(administrator);

        Assert.Empty(afterUnhide.Rows);
        Assert.NotNull(afterUnhide.Notice);
        Assert.Contains("back on its owner's history", afterUnhide.Notice!, StringComparison.Ordinal);

        Assert.True(afterUnhide.IsNarrowed);

        await HiddenRecordJourneys.OpenAsync(administrator, InteractivityPatience);

        HiddenRecordList clean = await HiddenRecordJourneys.ReadAsync(administrator);

        Assert.Empty(clean.Rows);
        Assert.False(clean.IsNarrowed);
        Assert.NotNull(clean.EmptySentence);
        Assert.Contains(
            "anywhere in the restaurant", clean.EmptySentence!, StringComparison.Ordinal);

        await HistoryJourneys.OpenAsync(alpha, InteractivityPatience);

        GuestHistory restored = await HistoryJourneys.ReadAsync(alpha);
        HistoryOrder back = Assert.Single(restored.Orders);

        Assert.Equal(theirs.GuestOrderIdentifier, back.GuestOrderIdentifier);
        Assert.Equal(tableLabel, back.TableLabel);
        Assert.Equal(expectedHiderTotal, back.PersonTotalText);
        Assert.Equal(HiddenOrderLineCount, back.LineCount);

        HistoryLine pieRestored = Assert.Single(back.Lines, line => line.Name == pie.Name);

        Assert.Equal(HiddenOrderPieQuantity, pieRestored.Quantity);
        Assert.Equal(Money(pie.PriceAmount * HiddenOrderPieQuantity), pieRestored.LineTotalText);

        await HistoryJourneys.OpenAsync(bravo, InteractivityPatience);

        GuestHistory bravoFinal = await HistoryJourneys.ReadAsync(bravo);
        HistoryOrder stillOne = Assert.Single(bravoFinal.Orders);

        Assert.Equal(bystanderOrder.GuestOrderIdentifier, stillOne.GuestOrderIdentifier);
    }

    [Fact]
    public async Task Admin_ResetsTotpUser_ForcesPasswordThenTotpReenrollment()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        StaffAccount kitchenAccount = await AdministrationJourneys.CreateStaffAccountAsync(
            administrator, "e2e.kitchen.twelve", "Twelve Kitchen", StaffRoles.Kitchen);

        IPage device = await instance.OpenIsolatedPageAsync(withVirtualAuthenticator: true);

        await AccountJourneys.SignInWithPasswordAsync(
            device, kitchenAccount.Username, kitchenAccount.TemporaryPassword);

        Assert.Contains(AccountRoutes.ForcedPasswordChange, device.Url, StringComparison.Ordinal);

        await AccountJourneys.CompleteForcedPasswordChangeAsync(
            device, kitchenAccount.TemporaryPassword, FirstKitchenPassword);

        IReadOnlyList<string> codesBeforeReset = await AccountJourneys.EnrollAuthenticatorAsync(device);

        Assert.Equal(AccountJourneys.ExpectedRecoveryCodeCount, codesBeforeReset.Count);
        Assert.All(codesBeforeReset, code => Assert.False(string.IsNullOrWhiteSpace(code)));

        Assert.Equal(1, await AccountJourneys.AddPasskeyAsync(device));

        await AccountJourneys.SignOutAsync(device);

        ManagedAccount before = await AdministrationJourneys.ReadAccountFactsAsync(
            administrator, kitchenAccount.PersonIdentifier);

        Assert.Equal(kitchenAccount.Username, before.Username);
        Assert.Equal("Active", Assert.Single(before.StatusChips));
        Assert.Equal("kitchen", Assert.Single(before.Roles));
        Assert.Contains("Password", before.Credentials);
        Assert.Contains("Authenticator", before.Credentials);

        CredentialReset reset = await AdministrationJourneys.ResetCredentialsAsync(
            administrator, kitchenAccount.PersonIdentifier);

        Assert.True(reset.ClearedAuthenticator);
        Assert.NotEqual(string.Empty, reset.TemporaryPassword);

        Assert.NotEqual(kitchenAccount.TemporaryPassword, reset.TemporaryPassword);

        ManagedAccount afterReset = await AdministrationJourneys.ReadAccountFactsAsync(
            administrator, kitchenAccount.PersonIdentifier);

        Assert.Contains("Must change password", afterReset.StatusChips);
        Assert.Contains("Must set up authenticator", afterReset.StatusChips);

        Assert.Contains("Active", afterReset.StatusChips);
        Assert.Equal("kitchen", Assert.Single(afterReset.Roles));

        Assert.Contains("Password", afterReset.Credentials);
        Assert.DoesNotContain("Authenticator", afterReset.Credentials);

        await AccountJourneys.SignInWithPasskeyAsync(device, kitchenAccount.Username);

        Assert.Contains(AccountRoutes.ForcedPasswordChange, device.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountRoutes.SignInTwoFactor, device.Url, StringComparison.Ordinal);

        await device.GotoAsync("/kitchen");

        Assert.Contains(AccountRoutes.ForcedPasswordChange, device.Url, StringComparison.Ordinal);
        Assert.Contains("ReturnUrl=%2Fkitchen", device.Url, StringComparison.Ordinal);

        await AccountJourneys.SignOutAsync(device);

        IPage terminal = await instance.OpenIsolatedPageAsync();

        await AccountJourneys.SignInWithPasswordAsync(
            terminal, kitchenAccount.Username, reset.TemporaryPassword);

        Assert.Contains(AccountRoutes.ForcedPasswordChange, terminal.Url, StringComparison.Ordinal);

        Assert.DoesNotContain(AccountRoutes.SignInTwoFactor, terminal.Url, StringComparison.Ordinal);

        await AccountJourneys.CompleteForcedPasswordChangeAsync(
            terminal, reset.TemporaryPassword, ReenrolledKitchenPassword);

        Assert.Contains(AccountRoutes.ForcedTotpEnrollment, terminal.Url, StringComparison.Ordinal);

        IReadOnlyList<string> codesAfterReset = await AccountJourneys.CompleteForcedTotpEnrollmentAsync(
            terminal, AccountJourneys.LandingPageMarker, "the landing page");

        Assert.Equal(AccountJourneys.ExpectedRecoveryCodeCount, codesAfterReset.Count);

        Assert.Empty(codesAfterReset.Intersect(codesBeforeReset, StringComparer.Ordinal));

        Assert.Equal("/", new Uri(terminal.Url).AbsolutePath);

        ILocator sessionName = terminal.Locator("span.session-name").First;
        await sessionName.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        Assert.Equal(kitchenAccount.Username, (await sessionName.InnerTextAsync()).Trim());

        await terminal
            .Locator("nav.app-session a.session-link[href='/kitchen']")
            .First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        await AccountJourneys.SignInWithPasskeyAsync(device, kitchenAccount.Username);

        Assert.Equal("/", new Uri(device.Url).AbsolutePath);
        Assert.DoesNotContain(AccountRoutes.SignInTwoFactor, device.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountRoutes.ForcedPasswordChange, device.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountRoutes.ForcedTotpEnrollment, device.Url, StringComparison.Ordinal);

        ManagedAccount restored = await AdministrationJourneys.ReadAccountFactsAsync(
            administrator, kitchenAccount.PersonIdentifier);

        Assert.Equal("Active", Assert.Single(restored.StatusChips));
        Assert.Contains("Password", restored.Credentials);
        Assert.Contains("Authenticator", restored.Credentials);
    }

    [Fact]
    public async Task Administration_IsOperableOnAHandheldViewport()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(handheld: true, cancellationToken: cancellationToken);
        IPage page = instance.Page;

        await AccountJourneys.CompleteSetupAsync(page, AccountJourneys.DefaultAdministrator);

        StaffAccount counter = await AdministrationJourneys.CreateStaffAccountAsync(
            page, HandheldCounterUsername, HandheldCounterDisplayName, StaffRoles.Counter);

        Assert.Equal(HandheldCounterUsername, counter.Username);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(page, HandheldTableLabel);
        MenuItemOnTheMenu menuItem = await AdministrationJourneys.CreateMenuItemAsync(
            page, HandheldMenuItemName, HandheldMenuItemPrice);

        List<HandheldReachReport> reports = [];

        foreach (string path in HandheldAdministrationIndexPaths)
        {
            reports.Add(await HandheldReach.MeasureAsync(page, path));
        }

        foreach (string path in HandheldDetailPaths(
            counter.PersonIdentifier, tableIdentifier, menuItem.Identifier))
        {
            reports.Add(await HandheldReach.MeasureAsync(page, path));
        }

        foreach (HandheldReachReport report in reports)
        {
            Assert.True(
                report.ClientWidth <= RestaurantInstance.HandheldViewportWidth
                    && report.ClientWidth >= RestaurantInstance.HandheldViewportWidth - ScrollbarAllowancePixels,
                $"{report.Path} was measured in a {report.ClientWidth}px viewport, and this scenario is"
                    + $" about {RestaurantInstance.HandheldViewportWidth}px. Either the context was not"
                    + " created handheld, or something resized it — and at any wider width every"
                    + " assertion below passes on a page nobody claims is reachable.");
        }

        int measured = reports.Sum(report => report.MeasuredCount);

        Assert.True(
            measured >= MinimumControlsMeasured,
            $"Only {measured} control(s) were measured across {reports.Count} surfaces, which is under the"
                + " floor. A selector this barrier reads has been renamed, or a page lost its list —"
                + " either way the assertions below are true of nothing.");

        string[] sideways = reports
            .Where(report => report.ScrollsSideways)
            .Select(report => report.DescribeOverflow())
            .ToArray();

        Assert.True(
            sideways.Length == 0,
            "§11.12: an administration surface must not scroll sideways on the screen it is used from."
                + $" {string.Join(" · ", sideways)}");

        MeasuredControl[] outOfReach = reports.SelectMany(report => report.OutOfReach).ToArray();

        Assert.True(
            outOfReach.Length == 0,
            "§11.12: a row's action is the full width of the foot of its card, so its box lies inside"
                + $" the viewport. Off the screen: {HandheldReach.Format(outOfReach)}. This is F-59, and"
                + " a control that has moved back into a right-hand column is how it returns.");

        MeasuredControl[] undersized = reports.SelectMany(report => report.Undersized).ToArray();

        Assert.True(
            undersized.Length == 0,
            $"§11.12: every control is at least {HandheldReach.MinimumTouchTargetPixels}px tall."
                + $" Shorter: {HandheldReach.Format(undersized)}.");
    }

    [Fact]
    public async Task Guest_ReadsTheMenuGroupedUnderItsHeadings()
    {
        SkipUnlessHarnessAvailable();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        const string tableLabel = "E2E Seventeen";
        const string starters = "Starters";
        const string puddings = "Puddings";
        const string soupDescription = "Lentil and smoked paprika, with sourdough.";
        const string pieDescription = "Bramley apple, short crust, served warm.";
        const string startersDescription = "Something to begin with.";

        GuestAccount guestAccount = new("e2e.menu.reader", "Menu Reader");

        await using RestaurantInstance instance =
            await _harness.StartInstanceAsync(cancellationToken: cancellationToken);

        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        Guid startersIdentifier = await AdministrationJourneys.CreateMenuSectionAsync(
            administrator, starters, startersDescription);
        Guid puddingsIdentifier = await AdministrationJourneys.CreateMenuSectionAsync(
            administrator, puddings);

        Assert.NotEqual(startersIdentifier, puddingsIdentifier);

        MenuItemOnTheMenu soup = await AdministrationJourneys.CreateMenuItemAsync(
            administrator, "Soup of the day", 6.50m, soupDescription, starters);
        MenuItemOnTheMenu pie = await AdministrationJourneys.CreateMenuItemAsync(
            administrator, "Apple pie", 5.00m, pieDescription, puddings);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        IPage guest = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, cancellationToken);

        IReadOnlyList<MenuCard> menu = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Count == 2,
            InteractivityPatience,
            "both menu items",
            cancellationToken);

        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        IReadOnlyList<(string SectionName, string? Description)> headings =
            await TableOrderJourneys.ReadMenuSectionDescriptionsAsync(guest);

        Assert.Equal(
            new[] { starters, puddings },
            headings.Select(heading => heading.SectionName).ToArray());

        Assert.Equal(
            new string?[] { startersDescription, null },
            headings.Select(heading => heading.Description).ToArray());

        MenuCard soupCard = Assert.Single(menu, card => card.Name == soup.Name);
        MenuCard pieCard = Assert.Single(menu, card => card.Name == pie.Name);

        Assert.Equal(starters, soupCard.SectionName);
        Assert.Equal(puddings, pieCard.SectionName);
        Assert.Equal(soupDescription, soupCard.Description);
        Assert.Equal(pieDescription, pieCard.Description);

        await TableOrderJourneys.ChooseAsync(guest, soup);

        ChosenItemDetail? detail = await TableOrderJourneys.ReadChosenItemDetailAsync(guest);

        Assert.NotNull(detail);
        Assert.Equal(soup.Name, detail.Name);
        Assert.Equal(soupDescription, detail.Description);

        MenuItemOnTheMenu second = await AdministrationJourneys.CreateMenuItemAsync(
            administrator, "Apple soup", 6.00m, sectionName: starters);

        IReadOnlyList<MenuCard> grown = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Count == 3,
            InteractivityPatience,
            "the third item to arrive on the open menu",
            cancellationToken);

        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        string[] startersInOrder = grown
            .Where(card => card.SectionName == starters)
            .Select(card => card.Name)
            .ToArray();

        Assert.Equal(new[] { soup.Name, second.Name }, startersInOrder);

        await AdministrationJourneys.SetMenuSectionVisibilityAsync(
            administrator, puddingsIdentifier, visibleToGuests: false);

        IReadOnlyList<MenuCard> withoutPuddings = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.All(card => card.SectionName != puddings),
            InteractivityPatience,
            $"the '{puddings}' heading to leave the menu",
            cancellationToken);

        Assert.Equal(new[] { starters }, withoutPuddings.Select(card => card.SectionName).Distinct().ToArray());
        Assert.DoesNotContain(withoutPuddings, card => card.Name == pie.Name);

        Assert.Equal(
            new[] { soup.Name, second.Name },
            withoutPuddings.Where(card => card.SectionName == starters).Select(card => card.Name).ToArray());
        Assert.All(withoutPuddings, card => Assert.True(card.IsAvailable));

        await AdministrationJourneys.SetMenuSectionVisibilityAsync(
            administrator, puddingsIdentifier, visibleToGuests: true);

        IReadOnlyList<MenuCard> restored = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Count == 3,
            InteractivityPatience,
            $"the '{puddings}' heading to return to the menu",
            cancellationToken);

        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        MenuCard restoredPie = Assert.Single(restored, card => card.Name == pie.Name);
        Assert.Equal(puddings, restoredPie.SectionName);
        Assert.Equal(pieDescription, restoredPie.Description);
        Assert.True(restoredPie.IsAvailable);

        await AdministrationJourneys.MoveMenuItemToSectionAsync(
            administrator, second.Identifier, puddingsIdentifier);

        IReadOnlyList<MenuCard> refiled = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Any(card => card.Name == second.Name && card.SectionName == puddings),
            InteractivityPatience,
            $"'{second.Name}' to move under the '{puddings}' heading",
            cancellationToken);

        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        Assert.Equal(
            new[] { soup.Name },
            refiled.Where(card => card.SectionName == starters).Select(card => card.Name).ToArray());

        Assert.Equal(
            new[] { pie.Name, second.Name },
            refiled.Where(card => card.SectionName == puddings).Select(card => card.Name).ToArray());

        MenuCard refiledCard = Assert.Single(refiled, card => card.Name == second.Name);
        Assert.True(refiledCard.IsAvailable);

        const string wines = "Wine list";

        Guid winesIdentifier = await AdministrationJourneys.CreateMenuSectionAsync(
            administrator, wines, "Chosen by the room, not by us.");

        Assert.NotEqual(Guid.Empty, winesIdentifier);

        IReadOnlyList<MenuHeadingOnTheIndex> index =
            await AdministrationJourneys.ReadMenuIndexAsync(administrator);

        Assert.Equal(
            new[] { starters, puddings, wines },
            index.Select(heading => heading.Name).ToArray());

        Assert.All(index, heading => Assert.True(heading.IsVisibleToGuests));

        Assert.Equal(
            new[] { soup.Name },
            Assert.Single(index, heading => heading.Name == starters).ItemNames.ToArray());

        Assert.Equal(
            new[] { pie.Name, second.Name },
            Assert.Single(index, heading => heading.Name == puddings).ItemNames.ToArray());

        Assert.Empty(Assert.Single(index, heading => heading.Name == wines).ItemNames);

        Assert.Equal(
            new[] { starters, puddings },
            (await TableOrderJourneys.ReadMenuSectionNamesAsync(guest)).ToArray());

        Assert.Equal(
            new[] { false, true, true },
            index.Select(heading => heading.OffersMoveUp).ToArray());

        Assert.Equal(
            new[] { true, true, false },
            index.Select(heading => heading.OffersMoveDown).ToArray());

        await AdministrationJourneys.MoveMenuHeadingAsync(
            administrator, startersIdentifier, up: false);

        IReadOnlyList<MenuCard> reordered = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Count > 0 && observed[0].SectionName == puddings,
            InteractivityPatience,
            $"the '{puddings}' heading to move above '{starters}'",
            cancellationToken);

        Assert.Equal(
            new[] { puddings, starters },
            reordered.Select(card => card.SectionName).Distinct().ToArray());

        Assert.Equal(
            new[] { pie.Name, second.Name },
            reordered.Where(card => card.SectionName == puddings).Select(card => card.Name).ToArray());

        Assert.Equal(
            new[] { soup.Name },
            reordered.Where(card => card.SectionName == starters).Select(card => card.Name).ToArray());

        IReadOnlyList<MenuHeadingOnTheIndex> movedIndex =
            await AdministrationJourneys.ReadMenuIndexAsync(administrator);

        Assert.Equal(
            new[] { puddings, starters, wines },
            movedIndex.Select(heading => heading.Name).ToArray());

        Assert.Equal(
            new[] { false, true, true },
            movedIndex.Select(heading => heading.OffersMoveUp).ToArray());

        await AdministrationJourneys.MoveMenuHeadingAsync(
            administrator, startersIdentifier, up: true);

        IReadOnlyList<MenuCard> restoredOrder = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed.Count > 0 && observed[0].SectionName == starters,
            InteractivityPatience,
            $"the '{starters}' heading to move back above '{puddings}'",
            cancellationToken);

        Assert.Equal(
            new[] { starters, puddings },
            restoredOrder.Select(card => card.SectionName).Distinct().ToArray());

        await AdministrationJourneys.MoveMenuItemAsync(administrator, second.Identifier, up: true);

        IReadOnlyList<MenuCard> itemMoved = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed
                .Where(card => card.SectionName == puddings)
                .Select(card => card.Name)
                .FirstOrDefault() == second.Name,
            InteractivityPatience,
            $"'{second.Name}' to move to the top of '{puddings}'",
            cancellationToken);

        Assert.Equal(
            new[] { second.Name, pie.Name },
            itemMoved.Where(card => card.SectionName == puddings).Select(card => card.Name).ToArray());

        Assert.Equal(
            new[] { starters, puddings },
            itemMoved.Select(card => card.SectionName).Distinct().ToArray());

        Assert.Equal(
            new[] { soup.Name },
            itemMoved.Where(card => card.SectionName == starters).Select(card => card.Name).ToArray());

        await AdministrationJourneys.MoveMenuItemAsync(administrator, second.Identifier, up: false);

        IReadOnlyList<MenuCard> itemRestored = await TableOrderJourneys.WaitForMenuAsync(
            guest,
            observed => observed
                .Where(card => card.SectionName == puddings)
                .Select(card => card.Name)
                .FirstOrDefault() == pie.Name,
            InteractivityPatience,
            $"'{pie.Name}' to return to the top of '{puddings}'",
            cancellationToken);

        Assert.Equal(
            new[] { pie.Name, second.Name },
            itemRestored.Where(card => card.SectionName == puddings).Select(card => card.Name).ToArray());

        IReadOnlyList<MenuHeadingOnTheIndex> finalIndex =
            await AdministrationJourneys.ReadMenuIndexAsync(administrator);

        Assert.Equal(
            new[] { starters, puddings, wines },
            finalIndex.Select(heading => heading.Name).ToArray());

        Assert.Equal(
            new[] { pie.Name, second.Name },
            Assert.Single(finalIndex, heading => heading.Name == puddings).ItemNames.ToArray());

        Assert.Equal(
            new[] { soup.Name },
            Assert.Single(finalIndex, heading => heading.Name == starters).ItemNames.ToArray());
    }

    private void SkipUnlessHarnessAvailable()
        => Assert.SkipUnless(
            _harness.SkipReason is null,
            _harness.SkipReason ?? "The end-to-end harness is unavailable.");

    private static async Task<string> HeadingAsync(IPage page)
        => (await page.Locator("h1").First.InnerTextAsync()).Trim();

    private static void AssertHandheldBarrier(HandheldReachReport report, string subject)
    {
        Assert.True(
            report.ClientWidth <= RestaurantInstance.HandheldViewportWidth
                && report.ClientWidth >= RestaurantInstance.HandheldViewportWidth - ScrollbarAllowancePixels,
            $"{subject} was measured in a {report.ClientWidth}px viewport, and this step is about"
                + $" {RestaurantInstance.HandheldViewportWidth}px. Either the counter's context was not"
                + " created handheld, or something resized it — and at any wider width every assertion"
                + " below passes on a page nobody claims is reachable.");

        Assert.NotEmpty(report.Reachable);

        Assert.False(
            report.ScrollsSideways,
            $"§11.12: {subject} must not scroll sideways on the screen it is worked from."
                + $" {report.DescribeOverflow()}. Census: {report.DescribeCensus()}.");

        Assert.True(
            report.OutOfReach.Count == 0,
            $"§11.12: every control on {subject} lies inside the viewport. Off the screen:"
                + $" {HandheldReach.Format(report.OutOfReach)}. This is F-59, and a control that has"
                + " moved back into a right-hand column is how it returns.");

        Assert.True(
            report.Undersized.Count == 0,
            $"§11.12: every control is at least {HandheldReach.MinimumTouchTargetPixels}px tall."
                + $" Shorter: {HandheldReach.Format(report.Undersized)}.");

        Assert.True(
            report.UndersizedText.Count == 0,
            $"§11.12: every text control is at least {HandheldReach.MinimumTextFontPixels}px."
                + $" Under it: {HandheldReach.Format(report.UndersizedText)}. This is F-118: the"
                + " control is rendered outside any arrangement `app.css` declares the floor against,"
                + " so it inherits a user-agent default and iOS Safari zooms the page around it.");
    }

    private static string JoinPath(Guid tableIdentifier, string token)
        => $"/table/{tableIdentifier:D}?token={Uri.EscapeDataString(token)}";

    private static string Money(decimal amount)
        => MoneyText.Format(amount, RestaurantInstance.CurrencyCode);

    private static TimeSpan RefreshPatience(int rotationSeconds)
        => TimeSpan.FromSeconds((rotationSeconds * 2) + 20);

    private static TimeSpan ReminderPatience(int reminderSeconds)
        => TimeSpan.FromSeconds(reminderSeconds)
            + (KitchenReminderService.ScanInterval * 2)
            + TimeSpan.FromSeconds(20);

    private static readonly TimeSpan QuietWatch = KitchenReminderService.ScanInterval * 3;

    private static readonly TimeSpan InteractivityPatience = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LiveUpdatePatience = TimeSpan.FromSeconds(30);

    private sealed record ArrangedService(
        IPage Kitchen,
        IPage Guest,
        Guid TableIdentifier,
        MenuItemOnTheMenu Soup,
        MenuItemOnTheMenu Pie);

    private static async Task<ArrangedService> ArrangeServiceAsync(
        RestaurantInstance instance,
        string tableLabel,
        GuestAccount guestAccount,
        CancellationToken cancellationToken)
    {
        IPage administrator = instance.Page;
        await AccountJourneys.CompleteSetupAsync(administrator, AccountJourneys.DefaultAdministrator);

        MenuItemOnTheMenu soup =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Soup of the day", 6.50m);
        MenuItemOnTheMenu pie =
            await AdministrationJourneys.CreateMenuItemAsync(administrator, "Steak pie", 14.00m);

        Guid tableIdentifier = await AdministrationJourneys.CreateTableAsync(administrator, tableLabel);
        byte[] joinSecret = await instance.ReadJoinSecretAsync(tableIdentifier, cancellationToken);

        IPage guest = await SeatGuestAsync(
            instance, tableIdentifier, joinSecret, guestAccount, cancellationToken);

        await KitchenJourneys.OpenAsync(administrator, InteractivityPatience);

        return new ArrangedService(administrator, guest, tableIdentifier, soup, pie);
    }

    private static Task<IPage> SeatGuestAsync(
        RestaurantInstance instance,
        Guid tableIdentifier,
        byte[] joinSecret,
        GuestAccount account,
        CancellationToken cancellationToken)
        => TableJourneys.SeatGuestAsync(
            instance, tableIdentifier, joinSecret, account, InteractivityPatience, cancellationToken);

    private static async Task WaitUntilTokenIsDeadAsync(
        DateTimeOffset mintedAt,
        int rotationSeconds,
        CancellationToken cancellationToken)
    {
        long mintedWindow = JoinTokenService.CurrentWindowIndex(mintedAt, rotationSeconds);
        DateTimeOffset deadAt = DateTimeOffset
            .FromUnixTimeSeconds((mintedWindow + 2) * rotationSeconds)
            .AddSeconds(1);

        TimeSpan remaining = deadAt - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private static void AssertShowingLiveJoinCode(
        string observedQrPath,
        byte[] joinSecret,
        Guid tableIdentifier,
        RestaurantInstance instance)
    {
        long newestWindowIndex = JoinTokenService.CurrentWindowIndex(
            DateTimeOffset.UtcNow, instance.TableJoinTokenRotationSeconds);

        string age = JoinQrCodes.Classify(
            observedQrPath,
            joinSecret,
            tableIdentifier,
            instance.PublicOrigin,
            newestWindowIndex);

        Assert.Contains(age, JoinQrCodes.LiveAges);
    }
}
