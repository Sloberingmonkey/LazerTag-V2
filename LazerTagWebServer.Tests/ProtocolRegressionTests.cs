using LazerTagHostLibrary;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed class ProtocolRegressionTests
{
	[Theory]
	[InlineData(false, 0x20)]
	[InlineData(true, 0x10)]
	public void Request_tag_report_uses_the_address_format_for_its_hosting_mode(
		bool enhanced, int expectedAddress)
	{
		var player = new TeamPlayerId(2, 1);
		var packet = PacketPacker.RequestTagReport(0x5A, player, 2, enhanced);

		Assert.Equal(expectedAddress, packet.Data[1].Data);
		Assert.Equal(0x07, packet.Data[2].Data);
		Assert.True(packet.ChecksumValid);
	}

	[Fact]
	public void Debrief_response_address_is_decoded_using_the_same_mode_as_the_request()
	{
		var standard = HostGun.DecodeDebriefPlayerAddress(0x10, useEnhancedAddressing: false);
		var enhanced = HostGun.DecodeDebriefPlayerAddress(0x10, useEnhancedAddressing: true);

		Assert.Equal(new TeamPlayerId(1, 1), standard);
		Assert.Equal(new TeamPlayerId(2, 1), enhanced);
	}

	[Theory]
	[InlineData(0x00, 0)]
	[InlineData(0x09, 9)]
	[InlineData(0x10, 10)]
	[InlineData(0x63, 63)]
	[InlineData(0x99, 99)]
	public void Team_tag_report_counts_are_bcd(int encoded, int expected)
	{
		Assert.True(HostGun.TryDecodeTeamTagCount((ushort)encoded, out int actual));
		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData(0x0A, 10)]
	[InlineData(0x8A, 90)]
	[InlineData(0xA0, 100)]
	public void Team_tag_report_count_accepts_legacy_noncanonical_bytes(int encoded, int expected)
	{
		Assert.True(HostGun.TryDecodeTeamTagCount((ushort)encoded, out int actual));
		Assert.Equal(expected, actual);
		Assert.False(HostGun.IsCanonicalTeamTagCountBcd((ushort)encoded));
	}

	[Fact]
	public void Team_tag_report_count_rejects_values_larger_than_one_byte()
	{
		Assert.False(HostGun.TryDecodeTeamTagCount(0x100, out int actual));
		Assert.Equal(0, actual);
	}

	[Fact]
	public void Standard_packets_preserve_two_digits_and_unlimited_and_reject_overflow()
	{
		var game = new GameDefinition
		{
			GameType = GameType.KingsTwoTeams, GameId = new RandomId(1),
			GameTimeMinutes = 20, Tags = 99, Shields = 99, Reloads = 0xFF,
		};
		var announcement = PacketPacker.AnnounceGame(game);
		Assert.Equal(0x99, announcement.Data[2].Data);
		Assert.Equal(0xFF, announcement.Data[3].Data);
		Assert.Equal(0x99, announcement.Data[4].Data);
		Assert.Equal(0x99, PacketPacker.Countdown(1, 99, 8, 8, 8).Data[1].Data);
		game.Tags = 200;
		Assert.Throws<ArgumentOutOfRangeException>(() => PacketPacker.AnnounceGame(game));
		Assert.Throws<ArgumentOutOfRangeException>(() => PacketPacker.Countdown(1, 100, 8, 8, 8));
		Assert.Throws<ArgumentOutOfRangeException>(() => PacketPacker.Countdown(1, 255, 8, 8, 8));
	}

	[Fact]
	public void Solo_debrief_requests_all_three_internal_player_groups()
	{
		var packet = PacketPacker.RequestTagReport(0x5A, new TeamPlayerId(1), 3,
			useEnhancedAddressing: true);

		Assert.Equal(0x0F, packet.Data[2].Data);
		Assert.True(packet.ChecksumValid);
	}
	[Theory]
	[InlineData(GameType.Respawn)]
	[InlineData(GameType.RespawnTwoTeams)]
	[InlineData(GameType.RespawnThreeTeams)]
	public void Enhanced_respawn_packet_enables_neutralize_and_supply_zone_revive(GameType gameType)
	{
		var definition = new GameDefinition
		{
			GameType = gameType,
			GameId = new RandomId(0x5A),
			GameTimeMinutes = 10,
			Tags = 10,
			Reloads = 20,
			Shields = 0,
			Mega = 0,
			StartingAmmo = 10,
			UseLtarEnhancedProtocol = true,
		};

		var packet = PacketPacker.AnnounceGameEnhanced(definition);

		Assert.Equal(PacketType.AnnounceGameEnhanced, packet.Type);
		Assert.True((packet.Data[7].Data & 0x80) != 0); // neutralize instead of permanent elimination
		Assert.True((packet.Data[8].Data & 0x20) != 0); // neutralize on tag
		Assert.True((packet.Data[8].Data & 0x10) != 0); // supply zone unneutralizes
		Assert.True(packet.ChecksumValid);
	}

}
