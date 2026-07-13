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
	public void Solo_debrief_requests_all_three_internal_player_groups()
	{
		var packet = PacketPacker.RequestTagReport(0x5A, new TeamPlayerId(1), 3,
			useEnhancedAddressing: true);

		Assert.Equal(0x0F, packet.Data[2].Data);
		Assert.True(packet.ChecksumValid);
	}
}
