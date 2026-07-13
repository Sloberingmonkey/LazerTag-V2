using System;
using System.Collections.Generic;
using System.Text;

namespace LazerTagHostLibrary
{
	public class LazerTagProtocol : IDisposable
	{
		#region Constructors/Destructor

		public LazerTagProtocol(HostGun hostGun, string portName)
		{
			_hostGun = hostGun;
			_serial = new LazerTagSerial();
			_serial.IoError += Serial_IoError;
			_serial.DataReceived += Serial_DataReceived;
			_serial.Connect(portName);
			_serial.Enqueue(new[] {(byte) '\r', (byte) '\n'});
		}

		public void Dispose()
		{
			if (_serial != null)
			{
				_serial.Disconnect();
				_serial.Dispose();
			}
		}

		#endregion

		#region Public Methods

		public bool SetDevice(string device)
		{
			_serial.Disconnect();
			return _serial.Connect(device);
		}

		/// <summary>True when the underlying transport connection is currently open.</summary>
		public bool IsConnected => _serial != null && _serial.IsConnected;

		public void TransmitSignature(Signature signature)
		{
			_serial.Enqueue(EncodeSignature(signature));
		}

		public void TransmitSignature(IEnumerable<Signature> signatures)
		{
			foreach (var signature in signatures)
			{
				TransmitSignature(signature);
			}
		}

		public void TransmitPacket(Packet packet)
		{
			var e = new PacketSendingEventArgs(packet);
			OnPacketSending(e);
			if (e.Cancel) return;

			Log.Add(Log.Severity.Debug, "TX {0}: {1}", packet.Type.ToString(), packet);

			// Encode all signatures into one contiguous byte[] and enqueue once.
			// Previously each signature was enqueued separately, causing:
			//   - one heap allocation per signature (13 for AnnounceGameEnhanced)
			//   - one 50ms sleep per signature in the serial write thread (650ms total)
			//   - one TCP write call per signature over WiFi
			// Now the whole packet is one allocation, one queue entry, one TCP write.
			_serial.Enqueue(EncodePacket(packet));
		}

		public void SendTag(TeamPlayerId teamPlayerId, int damage)
		{
			var signature = PacketPacker.Tag(teamPlayerId, damage);
			TransmitSignature(signature);
			Log.Add(Log.Severity.Information, "Shot {0} tags as player {1}.", damage, teamPlayerId);
		}

		public void SendRequestJoinGame(byte gameId, int preferredTeamNumber)
		{
			var taggerId = new RandomId();
			var packet = PacketPacker.RequestJoinGame(gameId, taggerId, preferredTeamNumber);
			TransmitPacket(packet);
			Log.Add(Log.Severity.Information,
				"Sending request to join game 0x{0:X2} with tagger ID 0x{1:X2}. Requesting team {2}", gameId, taggerId,
				preferredTeamNumber);
		}

		public void SendTextMessage(string message)
		{
			var packet = PacketPacker.TextMessage(message);
			TransmitPacket(packet);
		}

		#endregion

		#region Public Events

		public event EventHandler<LazerTagSerial.IoErrorEventArgs> IoError;

		protected void OnIoError(LazerTagSerial.IoErrorEventArgs e)
		{
			IoError?.Invoke(this, e);
		}

		public event EventHandler<PacketSendingEventArgs> PacketSending;

		protected virtual void OnPacketSending(PacketSendingEventArgs e)
		{
			PacketSending?.Invoke(this, e);
		}

		public class PacketSendingEventArgs : EventArgs
		{
			public PacketSendingEventArgs(Packet packet)
			{
				Cancel = false;
				Packet = packet;
			}

			public bool Cancel { get; set; }
			public Packet Packet { get; set; }
		}

		#endregion

		#region Private Methods

		private void Serial_DataReceived(object sender, LazerTagSerial.DataReceivedEventArgs e)
		{
			if (e.Data == null) return;

			foreach (var fragment in SplitSerialFragments(e.Data))
			{
				if (string.IsNullOrWhiteSpace(fragment)) continue;
				ProcessSerialFragment(fragment.Trim());
			}
		}

		private void ProcessSerialFragment(string fragment)
		{
			Log.Add(Log.Severity.Debug, "RAW SERIAL: {0}", fragment);
			var parts = fragment.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 1)
			{
				Log.Add(Log.Severity.Debug, "Received empty response.");
				return;
			}

			switch (parts[0].ToUpperInvariant())
			{
				case "RCV":
					if (parts.Length < 4)
					{
						Log.Add(Log.Severity.Debug, "Received truncated response, '{0}'.", fragment);
						break;
					}

					UInt16 data;
					byte bitCount;
					bool isBeacon;

					try
					{
						data = Convert.ToUInt16(parts[1], 16);
						bitCount = Convert.ToByte(parts[2], 16);
						isBeacon = (Convert.ToUInt16(parts[3], 16) != 0);
					}
					catch (FormatException formatException)
					{
						Log.Add($"Could not parse number in response, '{fragment}'.", formatException);
						break;
					}
					catch (OverflowException overflowException)
					{
						Log.Add($"Number overflow in response, '{fragment}'.", overflowException);
						break;
					}

					try
					{
						ProcessSignature(data, bitCount, isBeacon);
					}
					catch (Exception ex)
					{
						Log.Add($"ProcessSignature() failed for response, '{fragment}'.", ex);
					}
					break;
				case "RAW":
					Log.Add(Log.Severity.Debug, fragment);
					break;
				case "READY":
				case "OK":
					// benign relay chatter; do not treat as unrecognized noise
					break;
				case "ERROR":
					Log.Add(Log.Severity.Debug, fragment);
					break;
				default:
					Log.Add(Log.Severity.Debug, $"Received unrecognized response, '{fragment}'.");
					break;
			}
		}

        private static IEnumerable<string> SplitSerialFragments(string raw)
        {
            var results = new List<string>();

            if (string.IsNullOrWhiteSpace(raw))
                return results;

            string cleaned = raw.Replace("\r", "\n");

            string[] markers = new[]
            {
        "RCV ",
        "RAW ",
        "ERROR ",
        "READY",
        "OK Sent"
    };

            int i = 0;
            while (i < cleaned.Length)
            {
                int nextIndex = -1;
                string foundMarker = null;

                foreach (string marker in markers)
                {
                    int idx = cleaned.IndexOf(marker, i, StringComparison.Ordinal);
                    if (idx >= 0 && (nextIndex < 0 || idx < nextIndex))
                    {
                        nextIndex = idx;
                        foundMarker = marker;
                    }
                }

                if (nextIndex < 0)
                    break;

                int fragmentEnd = cleaned.Length;
                foreach (string marker in markers)
                {
                    int idx = cleaned.IndexOf(marker, nextIndex + foundMarker.Length, StringComparison.Ordinal);
                    if (idx >= 0 && idx < fragmentEnd)
                        fragmentEnd = idx;
                }

                string fragment = cleaned.Substring(nextIndex, fragmentEnd - nextIndex).Trim();
                if (!string.IsNullOrWhiteSpace(fragment))
                    results.Add(fragment);

                i = fragmentEnd;
            }

            if (results.Count == 0)
            {
                string trimmed = cleaned.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    results.Add(trimmed);
            }

            return results;
        }

        private void Serial_IoError(object sender, LazerTagSerial.IoErrorEventArgs e)
		{
			OnIoError(e);
		}

		private void ProcessSignature(UInt16 data, byte bitCount, bool isBeacon)
		{
			if (isBeacon)
			{
				ProcessBeaconSignature(data, bitCount);
			}
			else
			{
				ProcessDataSignature(data, bitCount);
			}
		}

		private static void ProcessBeaconSignature(UInt16 data, UInt16 bitCount)
		{
			switch (bitCount)
			{
				case 5:
					{
						var teamNumber = (data >> 3) & 0x3;
						var tagReceived = ((data >> 2) & 0x1) != 0;
						var flags = data & 0x3;

						var teamText = teamNumber == 0 ? "solo" : string.Format("team {0}", teamNumber);

						string typeText;
						var tagsReceivedText = "";
						if (!tagReceived && flags != 0)
						{
							var zoneType = (HostGun.ZoneType)flags;
							typeText = string.Format("zone beacon ({0})", zoneType);
						}
						else if (tagReceived)
						{
							typeText = "hit beacon";
							tagsReceivedText = string.Format(" Player received {0} tags.", flags + 1);
						}
						else
						{
							typeText = "beacon";
						}

						Log.Add(Log.Severity.Debug, "Received {0} {1}.{2}", teamText, typeText, tagsReceivedText);
						break;
					}
				case 9:
					{
						var tagReceived = ((data >> 8) & 0x1) != 0;
						var shieldActive = ((data >> 7) & 0x1) != 0;
						var tagsRemaining = (data >> 5) & 0x3;
						//var flags = (data >> 2) & 0x7;
						var teamNumber = data & 0x3;

						var teamText = teamNumber == 0 ? "solo" : string.Format("team {0}", teamNumber);
						var typeText = tagReceived ? "hit beacon" : "beacon";
						var shieldText = shieldActive ? " Shield active." : "";

						string tagsText;
						switch (tagsRemaining)
						{

							case 0x3:
								{
									tagsText = "50-100%";
									break;
								}
							case 0x2:
								{
									tagsText = "25-50%";
									break;
								}
							case 0x1:
								{
									tagsText = "1-25%";
									break;
								}
							default:
								{
									tagsText = "0";
									break;
								}
						}

						Log.Add(Log.Severity.Debug, "Received {0} {1}. {2} tags remaining.{3}", teamText, typeText, tagsText, shieldText);
						break;
					}
			}
		}

		private void ProcessDataSignature(UInt16 data, byte bitCount)
		{
			if (bitCount == 9)
			{
				if ((data & 0x100) == 0) // packet type
				{
					_incomingPacket = new Packet
					{
						PacketTypeSignature = new Signature(SignatureType.Packet, data),
					};
				}
				else // checksum
				{
					if (_incomingPacket == null)
					{
						if (TryRecoverPacketFromOrphanData(data))
						{
							return;
						}

						Log.Add(Log.Severity.Debug, "Stray checksum signature received.");
						_orphanData.Clear();
						return;
					}

					if (!(_incomingPacket.PacketTypeSignatureValid && _incomingPacket.DataValid))
					{
						Log.Add(Log.Severity.Debug, "Checksum received for invalid packet: {0}", _incomingPacket);
						_incomingPacket = null;
						_orphanData.Clear();
						return;
					}

					_incomingPacket.Checksum = new Signature(SignatureType.Checksum, data);

					if (_incomingPacket.ChecksumValid)
					{
						Log.Add(Log.Severity.Debug, "RX {0}: {1}", _incomingPacket.Type.ToString(), _incomingPacket);

						if (!_hostGun.ProcessPacket(_incomingPacket))
						{
							Log.Add(Log.Severity.Debug, "ProcessPacket() returned false: {0}", _incomingPacket);
						}
					}
					else
					{
						Log.Add(Log.Severity.Debug, "Invalid checksum received. {0}", _incomingPacket);
					}

					_incomingPacket = null;
					_orphanData.Clear();
				}
			}
			else if (bitCount == 8) // data
			{
				if (_incomingPacket == null || !_incomingPacket.PacketTypeSignatureValid)
				{
					_orphanData.Add(new Signature(SignatureType.Data, data));
					if (_orphanData.Count > 16) _orphanData.RemoveAt(0);
					Log.Add(Log.Severity.Debug, "Buffered orphan data signature. 0x{0:X2} ({1})", data, bitCount);
					_incomingPacket = null;
					return;
				}

				_incomingPacket.Data.Add(new Signature(SignatureType.Data, data));
			}
			else if (bitCount == 7) // tag
			{
				_incomingPacket = null;
				_orphanData.Clear();
				ProcessTag(new Signature(SignatureType.Tag, data, bitCount));
			}
			else
			{
				Log.Add(Log.Severity.Debug, "Stray data packet received. 0x{0:X2} ({1})", data, bitCount);
			}
		}

		private bool TryRecoverPacketFromOrphanData(UInt16 checksumData)
		{
			if (_orphanData.Count < 1) return false;

			var expectedType = _hostGun.GetExpectedDebriefPacketTypeForRecovery();
			if (!expectedType.HasValue) return false;

			var recoveredPacket = new Packet(expectedType.Value);
			foreach (var dataSignature in _orphanData)
			{
				recoveredPacket.Data.Add(new Signature(SignatureType.Data, dataSignature.Data));
			}
			recoveredPacket.Checksum = new Signature(SignatureType.Checksum, checksumData);

			if (!recoveredPacket.ChecksumValid)
			{
				return false;
			}

			Log.Add(Log.Severity.Information, "Recovered {0} from orphan LTAR data during summary: {1}", recoveredPacket.Type, recoveredPacket);
			if (!_hostGun.ProcessPacket(recoveredPacket))
			{
				Log.Add(Log.Severity.Debug, "ProcessPacket() returned false for recovered packet: {0}", recoveredPacket);
			}
			_orphanData.Clear();
			return true;
		}

		private static void ProcessTag(Signature signature)
		{
			var teamPlayerId = TeamPlayerId.FromPacked23((UInt16) ((signature.Data >> 2) & 0x1f));
			var strength = (signature.Data & 0x3) + 1;
			Log.Add(Log.Severity.Debug, "Received shot from player {0} with {1} tags.", teamPlayerId.ToStringFull(true), strength);
		}

		/// <summary>
		/// Encodes a single IR signature as a "CMD 10 data bits beacon\r\n" ASCII line.
		/// </summary>
		private static byte[] EncodeSignature(Signature signature)
		{
			if (signature == null) throw new ArgumentException("signature");

			var isBeacon = signature.Type == SignatureType.Beacon;

			var data = signature.Data;
			byte bitCount;

			switch (signature.Type)
			{
				case SignatureType.Packet:
					data &= 0xff;
					bitCount = 9;
					break;
				case SignatureType.Checksum:
					data &= 0xff;
					data |= 0x100; // set the 9th bit
					bitCount = 9;
					break;
				default:
					bitCount = signature.BitCount;
					break;
			}

			var command = string.Format("CMD 10 {0:X} {1:X} {2:X}\r\n", data, bitCount, isBeacon ? 1 : 0);

			return Encoding.ASCII.GetBytes(command);
		}

		/// <summary>
		/// Encodes all signatures in a packet into one contiguous byte[] so the
		/// entire packet is enqueued and written as a single operation.
		/// Avoids one allocation + one queue entry + one TCP write per signature.
		/// </summary>
		private static byte[] EncodePacket(Packet packet)
		{
			var signatures = packet.Signatures; // one List alloc here - acceptable, packet is being sent
			// Pre-size: each CMD line is at most 24 bytes ("CMD 10 1FF F 1\r\n" = 16 chars max)
			var sb = new StringBuilder(signatures.Count * 20);
			foreach (var sig in signatures)
			{
				var isBeacon = sig.Type == SignatureType.Beacon;
				var data = sig.Data;
				byte bitCount;
				switch (sig.Type)
				{
					case SignatureType.Packet:
						data &= 0xff;
						bitCount = 9;
						break;
					case SignatureType.Checksum:
						data &= 0xff;
						data |= 0x100;
						bitCount = 9;
						break;
					default:
						bitCount = sig.BitCount;
						break;
				}
				sb.AppendFormat("CMD 10 {0:X} {1:X} {2:X}\r\n", data, bitCount, isBeacon ? 1 : 0);
			}
			return Encoding.ASCII.GetBytes(sb.ToString());
		}

		#endregion

		#region Private Fields

		private readonly LazerTagSerial _serial;
		private readonly HostGun _hostGun;
		private Packet _incomingPacket;
		private readonly List<Signature> _orphanData = new List<Signature>();

		#endregion
	}
}
