# Newfarm

An out-of-band session directory for host migration, over UDP.

Relay services assign the room credential themselves, kill the room when its host goes, and route host-to-client only. So the moment a host drops there is no channel left between the surviving players, and no way for them to be told where to regroup. Either they already hold something that outlives the host, or they are stranded.

Newfarm is that something. It owns a session identity that outlives any particular room, and it is the only writer of the fact "this is where the session lives now". That is what removes split brain structurally rather than by convention.

It knows nothing about any relay. The credential it hands out is opaque text plus a tag naming the service, so a room code, an allocation id, or a host and port all fit without newfarm being taught about any of them.

## The flow

1. The host asks newfarm for a session and gets back a `SessionId`.
2. The host distributes that id to its clients over the game connection.
3. The host creates a room on whatever relay it uses and publishes the credential to newfarm.
4. The host heartbeats newfarm for as long as it is hosting.
5. The host drops. Its clients come to newfarm with the session id.
6. Newfarm elects the first one to arrive, and tells it to host.
7. That peer creates a **new** room, gets a **new** credential, and publishes it.
8. Newfarm hands that credential to every other waiting peer, and goes on serving it for a while so late arrivals are not stranded.

## Using the client

```csharp
NewfarmClient directory = new(new NewfarmClientConfig(new IPEndPoint(directoryAddress, 47778)));

// Host: get a session, distribute the id, publish where the room is.
directory.SessionCreated += identity => SendToEveryClient(identity.SessionId);
directory.CreateSession();
// ... create your room, then ...
directory.PublishCredential("blitzrelay", roomCode);

// Client: hold the id, and come back with it when the host is lost.
directory.CredentialAvailable += credential => JoinRoom(credential.Credential);
directory.ElectedToHost += epoch => { CreateRoom(); directory.PublishCredential("blitzrelay", roomCode); };
directory.AwaitSession(new NewfarmSessionIdentity(sessionId, epoch: 0));

// Every frame, or every network tick. The client owns no threads.
directory.Poll();
```

Three calls carry most of the weight:

- **`ReportCredentialUnreachable()`** when a join actually fails. This is not optional politeness: it is the only way newfarm can learn that a host is unreachable while its heartbeats keep arriving. A peer that notices the host is gone before newfarm does will simply be handed back the room that just died, which is right for a link blip and useless for a dead host. The loop is *await, try what you are given, report it if it is dead*.
- **`SurrenderHosting()`** when a peer stops hosting but stays online, the player who left the match. It hands over at once rather than waiting for a heartbeat that is never going to lapse.
- **`DeclineElection()`** when a peer is told to host and cannot. It passes the job straight on, and that peer is skipped by the next election rather than being handed the same job again.

Answer **`HostingChallenged`** by publishing a credential (creating a fresh room if the one you had is gone), or by surrendering. Saying nothing is also an answer, and the wrong one.

## Running the directory

```bash
dotnet run --project Newfarm.Host -- --port 47778
```

`--help` lists every option: timings, the challenge settings, and the abuse limits. The limits worth knowing about before a real deployment:

- `--hostless-grace-ms` must outlast the slowest client's own discovery that the host has gone. That discovery comes from the relay, not from newfarm, and a relay can take tens of seconds. A client arriving after the session was forgotten cannot be told anything at all.
- `--address-rate` and `--address-burst` are deliberately loose, because an address is not a peer. A school, an office, or a carrier-grade NAT puts hundreds of unrelated players behind one. Raise them rather than let them turn real players away.

## Things worth knowing

**A heartbeat is not proof of hosting.** A peer whose game wedged, whose room died, or whose route to the relay failed while its route to newfarm did not, keeps heartbeating perfectly while hosting nothing. So peers report what they cannot reach, newfarm asks the host to prove otherwise by publishing, and a host that stays silent for long enough is stood down mid-heartbeat. A host that *answers* keeps the session however many peers are complaining: newfarm cannot tell a host nobody can reach from a peer that can reach nobody, and only one of those is worth taking a session away over.

**The session id is a bearer token.** It has to reach every client for the session to survive its host, and whoever holds it can be elected to host and can say where the session moved to. It is 64 unguessable bits from a cryptographic source, and that is the whole of the protection. Do not log it.

**Newfarm will not amplify.** Every request is padded to at least the size of the largest possible reply, and anything shorter is dropped without an answer, so putting somebody else's address on a request buys an attacker nothing.

## Scale

Measured on one machine, `NEWFARM_SCALE=1 NEWFARM_SCALE_SESSIONS=<n> dotnet test`, with the concurrent-session cap lifted. Round trip is what a peer waits for an answer while the sweep walks every session held.

| Sessions held | Memory | Round trip median | 99th | Worst |
|---|---|---|---|---|
| 10,000 | 2.8 MB | 0.12 ms | 0.25 ms | 0.81 ms |
| 100,000 | 31 MB | 0.12 ms | 0.21 ms | 0.67 ms |
| 250,000 | 75 MB | 0.11 ms | 0.20 ms | 1.21 ms |
| 1,000,000 | 219 MB | 0.05 ms | 0.58 ms | 0.97 ms |

Sessions were opened at roughly 45,000 a second throughout. A separate run held 20,000 sessions under 540,000 heartbeats over 30 seconds, at 17,500 a second, and lost none of them.

The shape to take from this: a session in the steady state is one small object, a couple of hundred bytes. The collections a migration needs, the waiting set and the reports against a host, are allocated only while one is under way and dropped when it ends, so a directory full of healthy sessions pays for none of them. That is also what keeps the sweep flat: it walks a million sessions without leaving them for side lookups, and the worst answer a peer waited on while it did was under a millisecond. The loop is still one thread, so the ceiling is how fast it can answer, not how much it can hold.

## Unity

The client is its own project, `Newfarm.Client`, built for `netstandard2.1` and held to C# 9 so Unity can compile it directly as source. That is how the Nucleus engine's Unity integration consumes it: the project folder is junctioned into the Unity project under `Assets\Nucleus\Integrations\Newfarm Client` and the editor builds it as its own assembly, no DLL involved. It references nothing beyond the core BCL and holds no runtime reflection, so IL2CPP has nothing to strip out from under it, and the directory ships separately in `Newfarm.Server`, so a game carrying the client carries no server code.

This repository holds only the client and the directory. What a game does with them, being elected, adopting the world it kept and finding where the session moved to, is engine work: for Nucleus that is `Nucleus.Integrations.Newfarm`, which junctions in beside this one and drives whatever carries the session through an `ISessionHost` of its own. Newfarm still knows nothing about any engine, any relay, or any credential it hands out.

What has been checked: the Unity editor compiles it from source, and the assemblies that consume it resolve against it. What has **not** been done: an IL2CPP build has never been run on a device. And `System.Net.Sockets` is not available on WebGL, so the client cannot run there as it stands.

## Layout

| Project | What it is |
|---|---|
| `Newfarm.Client` | The client and the wire format. `netstandard2.1` and `net8.0`, C# 9 so Unity can compile it as source. |
| `Newfarm.Server` | The directory itself: sessions, elections, challenges, limits. |
| `Newfarm.Host` | The directory as a standalone console service. |
| `Newfarm.Tests` | End-to-end tests: a real server on a loopback port, real clients, real datagrams. |

Migration is tested twice over, and neither suite lives here. `Nucleus.Tests` drives the coordinator against a real directory over a real socket with the service faked, which is fast and covers every branch; the Blitz Relay repository drives the same coordinator through a live relay while hosts crash, quit and wedge.
