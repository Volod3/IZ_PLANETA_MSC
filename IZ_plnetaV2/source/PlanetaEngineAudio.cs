using System;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace Planeta
{
	// Звук двигуна Іж Планета (двотактний одноциліндровий: рівний "дзижчачий" звук, голосніший під газом).
	//
	// Що виправлено порівняно зі штатним відтворенням:
	//  1) Шов семпла. Штатний loop=true повторює клип "встик": якщо кінець не збігається з початком
	//     (а в стиснутих .wav/.ogg так майже завжди) - чути клік/стук. Тут кожен шар грає двома голосами по
	//     черзі: наприкінці клипа наступний голос плавно (equal-power) вростає, поточний так само згасає.
	//     Самі клипи не змінюються - шов просто ніколи не звучить на повній гучності.
	//  2) Мікс і pitch рахуються з обертів Drivetrain, згладжені в часі: смикання RPM на відсічці більше
	//     не перетворюється на тріск/биття, а ривки pitch обмежені швидкістю.
	//  3) Голоси мають високий пріоритет (не "крадуться" іншими звуками гри) і без доплера
	//     (доплер на швидкому мопеді дає плаваючий pitch і хрипи).
	//  4) Оригінальні AudioSource (діти 31/32) лише глушаться (mute) - гра далі ними керує, але вони не чутні.
	//     Якщо поле rpm у Drivetrain не знайдено - працюємо в режимі "слідувати за оригіналом":
	//     беремо його volume/pitch, але все одно з нашим безшовним повтором.
	public class PlanetaEngineAudio : MonoBehaviour
	{
		// ======================= НАЛАШТУВАННЯ (крутити тут) =======================
		private const float MasterGain = 0.85f;       // загальна гучність (запас, щоб сума голосів не кліпінгувала)
		private const float CrossFade = 0.20f;        // с (по часу клипа): перекриття кінця й початку семпла
		private const float BlendStart = 0.12f;       // нормалізовані оберти (0..1), з яких підмішується "high"
		private const float BlendEnd = 0.70f;         // ... і при яких "idle" повністю вимкнений
		private const float IdlePitchMin = 0.92f;     // pitch idle-шару на холостих
		private const float IdlePitchMax = 1.22f;     // pitch idle-шару наприкінці його діапазону
		private const float HighPitchMin = 0.86f;     // pitch high-шару на початку підмішування
		private const float HighPitchMax = 1.16f;     // pitch high-шару на максимумі (повний газ)
		private const float LoadMin = 0.45f;          // множник гучності без газу (на викаті двигун тихіший)
		private const float LoadAttack = 7.0f;        // 1/с: як швидко "газ" наростає в звуці
		private const float LoadRelease = 2.2f;       // 1/с: як швидко звук спадає після скидання газу
		private const float ThrottleBlend = 0.90f;    // яку частку "high" дає повний газ, навіть якщо оберти ще не виросли
		private const float SpeedForFull = 14f;       // м/с: швидкість, при якій швидкісна складова на максимумі
		private const float SpeedBlend = 0.55f;       // скільки "high" дає сама швидкість (на викаті на ходу)
		private const float CoastHigh = 0.30f;        // множник гучності "high" без газу (на викаті їзда звучить тихіше й нижче)
		private const float RpmSmoothTime = 0.10f;    // с, згладжування RPM (прибирає смикання на відсічці)
		private const float PitchRate = 2.0f;         // макс. зміна pitch за секунду
		private const float VolRate = 5.0f;           // макс. зміна гучності за секунду
		private const float RunningRpmFactor = 0.5f;  // двигун "працює", коли rpm > minRPM * це число
		private const float EngineDoppler = 0f;       // 0 = без доплера
		// Пріоритет голосів = як у оригінального джерела (штатний, без підвищення)
		private const float RideVolume = 1.0f;       // множник гучності, коли ти їдеш на мопеді
		private const float StandVolume = 0.65f;     // множник гучності, коли мопед стоїть/працює без тебе
		private const float RideSpatial = 0.55f;     // 3D-ність звуку, коли ти їдеш (0 = 2D, 1 = повністю 3D)
		private const float RideMaxDistance = 3f;    // м: ближче цього до слухача + ти на Jonnez = ти на цьому мопеді
		private const float CullDistance = 120f;      // далі цієї відстані до слухача голоси вимкнені
		private const bool DebugLog = false;          // true: раз на секунду друкувати rpm/pitch/гучність у консоль

		// ======================= Шар (один клип, два голоси з кросфейдом) =======================
		private sealed class Layer
		{
			public AudioClip Clip;
			public AudioSource Orig;     // оригінальне джерело (для налаштувань 3D і режиму "слідувати")
			public float Vol;            // поточна (згладжена) гучність
			public float Pitch = 1f;     // поточний (згладжений) pitch

			private readonly AudioSource[] src = new AudioSource[2];
			private readonly float[] pos = new float[2];        // позиція у клипі, с
			private readonly float[] startedAt = new float[2];
			private readonly bool[] started = new bool[2];
			private float fade;

			public void Build(Transform root, string label)
			{
				fade = Mathf.Min(CrossFade, Clip.length * 0.12f);   // high.wav лише ~0.53 с -> там перекриття ~0.06 с, у idle.wav (~3 с) - до 0.20 с
				Vector3 at = Orig != null ? Orig.transform.position : root.position;
				for (int i = 0; i < 2; i++)
				{
					GameObject go = new GameObject("Planeta" + label + "Voice" + i);
					go.transform.SetParent(root, false);
					go.transform.position = at;
					AudioSource s = go.AddComponent<AudioSource>();
					s.clip = Clip;
					s.loop = false;
					s.playOnAwake = false;
					s.volume = 0f;
					s.pitch = 1f;
					s.priority = Orig != null ? Orig.priority : 128;
					s.dopplerLevel = EngineDoppler;
					s.spatialBlend = Orig != null ? Orig.spatialBlend : 1f;
					s.minDistance = Orig != null ? Orig.minDistance : 3f;
					s.maxDistance = Orig != null ? Orig.maxDistance : 80f;
					s.rolloffMode = Orig != null ? Orig.rolloffMode : AudioRolloffMode.Logarithmic;
					s.spread = Orig != null ? Orig.spread : 0f;
					src[i] = s;
				}
				for (int i = 0; i < 2; i++) startedAt[i] = -10f;
			}

			public void SetSpatial(float k)
			{
				if (Orig == null) return;
				float v = Mathf.Lerp(Orig.spatialBlend, RideSpatial, k);
				for (int i = 0; i < 2; i++) if (src[i] != null) src[i].spatialBlend = v;
			}

			public void StopAll()
			{
				for (int i = 0; i < 2; i++)
				{
					if (started[i] && src[i] != null) src[i].Stop();
					started[i] = false;
				}
			}

			private bool Alive(int i)
			{
				if (!started[i]) return false;
				if (Time.realtimeSinceStartup - startedAt[i] < 0.15f) return true;   // Play() ще "розганяється"
				if (pos[i] > Clip.length + 0.1f || !src[i].isPlaying) { started[i] = false; return false; }
				return true;
			}

			private void Begin(int i)
			{
				AudioSource s = src[i];
				s.Stop();
				s.volume = 0f;          // перші семпли - з нуля, без кліку
				s.pitch = Pitch;
				s.Play();
				started[i] = true;
				startedAt[i] = Time.realtimeSinceStartup;
				pos[i] = 0f;
			}

			public void Tick(float dt, float wantVol, float wantPitch)
			{
				if (AudioListener.pause) return;
				Vol = Mathf.MoveTowards(Vol, wantVol, VolRate * dt);
				Pitch = Mathf.MoveTowards(Pitch, wantPitch, PitchRate * dt);
				if (Vol < 0.0008f && wantVol < 0.0008f) { StopAll(); return; }

				float len = Clip.length;
				float now = Time.realtimeSinceStartup;

				// позиція кожного голосу: власний інтегратор (з урахуванням pitch), уточнюємо по timeSamples
				for (int i = 0; i < 2; i++)
				{
					if (!started[i]) continue;
					pos[i] += dt * Pitch;
					if (now - startedAt[i] > 0.15f)
					{
						int ts = src[i].timeSamples;
						if (ts > 0) pos[i] = ts / (float)Clip.frequency;
					}
				}

				// ведучий голос = той, що далі просунувся; коли він доходить до зони шва - запускаємо другий
				int lead = -1;
				float leadPos = -1f;
				for (int i = 0; i < 2; i++)
				{
					if (Alive(i) && pos[i] > leadPos) { lead = i; leadPos = pos[i]; }
				}
				if (lead < 0) { Begin(0); lead = 0; leadPos = 0f; }
				int other = 1 - lead;
				if (leadPos >= len - fade && !Alive(other)) Begin(other);

				// гучність кожного голосу: плавне вростання на початку й згасання наприкінці (sin -> сума потужностей = 1)
				for (int i = 0; i < 2; i++)
				{
					if (!Alive(i)) continue;
					float head = Mathf.Sin(0.5f * Mathf.PI * Mathf.Clamp01(pos[i] / fade));
					float tail = Mathf.Sin(0.5f * Mathf.PI * Mathf.Clamp01((len - pos[i]) / fade));
					src[i].volume = Vol * Mathf.Min(head, tail);
					src[i].pitch = Pitch;
				}
			}
		}

		// ======================= Компонент =======================
		private Layer idle, high;
		private Drivetrain drive;
		private FieldInfo fRpm, fMaxRpm, fThrottle, fRunning;
		private bool ready;
		private bool followOriginal;
		private float rpmSmooth;
		private float observedMax;
		private float nextListenerCheck;
		private float nextDebug;
		private Transform listener;
		private float ride;            // 0 = мопед стоїть, 1 = ти їдеш (згладжено)
		private bool riding;
		private float nextRideCheck;
		private Rigidbody body;        // для швидкості мопеда (оберти/газ можуть не мінятися так, як чекаємо)
		private float loadSmooth;      // згладжений газ 0..1 (швидко росте, повільно спадає)
		private float speedSmooth;     // згладжена швидкість, м/с

		public void Setup(AudioClip idleClip, AudioClip highClip, int idleChild, int highChild)
		{
			if (ready) return;
			try
			{
				if (idleClip == null || highClip == null || idleClip.length < 0.3f || highClip.length < 0.3f)
				{
					ModConsole.Print("[Planeta][Audio] Немає клипів двигуна - лишаю звук оригіналу.");
					return;
				}

				// оригінальні джерела беремо ДО створення своїх голосів (вони додаються в кінець списку дітей)
				Transform root = transform;
				AudioSource oi = SrcOf(root, idleChild);
				AudioSource oh = SrcOf(root, highChild);

				body = GetComponent<Rigidbody>();
				DumpSources(root);
				drive = GetComponent<Drivetrain>();
				fRpm = FindNumField("rpm");
				fMaxRpm = FindNumField("maxRPM");
				fThrottle = FindNumField("throttle");
				fRunning = FindBoolField("startedEngine", "engineRunning", "started");
				followOriginal = drive == null || fRpm == null;
				if (followOriginal && (oi == null || oh == null))
				{
					ModConsole.Print("[Planeta][Audio] Немає ні Drivetrain.rpm, ні оригінальних AudioSource - лишаю звук оригіналу.");
					return;
				}

				idle = new Layer();
				idle.Clip = idleClip;
				idle.Orig = oi;
				idle.Build(root, "Idle");
				high = new Layer();
				high.Clip = highClip;
				high.Orig = oh;
				high.Build(root, "High");

				if (oi != null) oi.mute = true;   // штатні джерела глушимо - звучать тільки наші голоси
				if (oh != null) oh.mute = true;

				ready = true;
				ModConsole.Print("[Planeta][Audio] Режим: " + (followOriginal ? "слідую за оригіналом (поле rpm не знайдено)" : "за обертами Drivetrain")
					+ " | rpm=" + Name(fRpm) + " maxRPM=" + Name(fMaxRpm) + " throttle=" + Name(fThrottle) + " running=" + Name(fRunning));
			}
			catch (Exception e)
			{
				ModConsole.Print("[Planeta][Audio] Setup: " + e);
			}
		}

		private void Update()
		{
			if (!ready) return;
			try
			{
				float now = Time.realtimeSinceStartup;
				float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);

				// далекі мопеди (чужі в мультиплеєрі) не повинні займати голоси
				if (now >= nextListenerCheck)
				{
					nextListenerCheck = now + 0.5f;
					Camera cam = Camera.main;
					listener = cam != null ? cam.transform : null;
				}
				if (now >= nextRideCheck)
				{
					nextRideCheck = now + 0.2f;
					riding = false;
					FsmString v = FsmVariables.GlobalVariables.FindFsmString("PlayerCurrentVehicle");
					if (v != null && v.Value == "Jonnez" && listener != null
						&& (listener.position - transform.position).sqrMagnitude < RideMaxDistance * RideMaxDistance) riding = true;
				}
				ride = Mathf.MoveTowards(ride, riding ? 1f : 0f, dt * 4f);

				if (listener != null && (listener.position - transform.position).sqrMagnitude > CullDistance * CullDistance)
				{
					Silence();
					return;
				}

				float wantIdleVol, wantIdlePitch, wantHighVol, wantHighPitch;
				if (followOriginal)
				{
					wantIdleVol = FollowVol(idle.Orig);
					wantIdlePitch = FollowPitch(idle.Orig);
					wantHighVol = FollowVol(high.Orig);
					wantHighPitch = FollowPitch(high.Orig);
				}
				else
				{
					float rpm = ReadF(fRpm, 0f);
					float minRpm = Mathf.Max(1f, drive.minRPM);
					if (rpm > observedMax) observedMax = rpm;
					float maxRpm = ReadF(fMaxRpm, 0f);
					if (maxRpm <= minRpm) maxRpm = Mathf.Max(observedMax, minRpm * 1.6f);

					bool running = fRunning != null
						? (bool)fRunning.GetValue(drive) && rpm > 1f
						: rpm > minRpm * RunningRpmFactor;

					rpmSmooth = Mathf.Lerp(rpmSmooth, rpm, 1f - Mathf.Exp(-dt / RpmSmoothTime));
					float n = Mathf.Clamp01((rpmSmooth - minRpm) / (maxRpm - minRpm));
					float thr = fThrottle != null ? Mathf.Clamp01(ReadF(fThrottle, 0f)) : n;

					// Газ: швидко наростає, повільно спадає - різниця "їду під газом" / "відпустив" чутна.
					loadSmooth = Mathf.MoveTowards(loadSmooth, thr, (thr > loadSmooth ? LoadAttack : LoadRelease) * dt);
					// Газ -> high, без газу (холості) -> idle. Швидкість на мікс НЕ впливає: на викаті на ходу грає idle.
					float mix = Mathf.Max(n, loadSmooth);
					float loadVol = loadSmooth;

					// зона кросфейду idle -> high (smoothstep + equal-power)
					float t = Mathf.Clamp01((mix - BlendStart) / (BlendEnd - BlendStart));
					float x = t * t * (3f - 2f * t);
					float gIdle = Mathf.Cos(x * Mathf.PI * 0.5f);
					float gHigh = Mathf.Sin(x * Mathf.PI * 0.5f);

					float placeMul = Mathf.Lerp(StandVolume, RideVolume, ride);
					float master = running ? MasterGain * placeMul * Mathf.Lerp(LoadMin, 1f, loadVol) : 0f;
					wantIdleVol = master * gIdle;
					wantHighVol = master * gHigh;
					wantIdlePitch = Mathf.Lerp(IdlePitchMin, IdlePitchMax, Mathf.Clamp01(mix / BlendEnd));
					wantHighPitch = Mathf.Lerp(HighPitchMin, HighPitchMax, Mathf.Clamp01(mix));

					if (DebugLog && now >= nextDebug)
					{
						nextDebug = now + 1f;
						ModConsole.Print("[Planeta][Audio] rpm=" + rpm.ToString("0") + " (min " + minRpm.ToString("0") + ", max " + maxRpm.ToString("0")
							+ ") n=" + n.ToString("0.00") + " thr=" + thr.ToString("0.00") + " load=" + loadSmooth.ToString("0.00")
							+ " mix=" + mix.ToString("0.00") + " run=" + running
							+ " | idle v=" + idle.Vol.ToString("0.00") + " p=" + idle.Pitch.ToString("0.00")
							+ " | high v=" + high.Vol.ToString("0.00") + " p=" + high.Pitch.ToString("0.00"));
					}
				}

				idle.SetSpatial(ride);
				high.SetSpatial(ride);
				idle.Tick(dt, wantIdleVol, wantIdlePitch);
				high.Tick(dt, wantHighVol, wantHighPitch);
			}
			catch (Exception e)
			{
				// не спамимо помилкою щокадру: повертаємо штатний звук і вимикаємось
				ModConsole.Print("[Planeta][Audio] Помилка, повертаю звук оригіналу: " + e);
				ready = false;
				Silence();
				if (idle != null && idle.Orig != null) idle.Orig.mute = false;
				if (high != null && high.Orig != null) high.Orig.mute = false;
			}
		}

		private void OnDisable()
		{
			if (ready) Silence();
		}

		private void Silence()
		{
			if (idle != null) { idle.StopAll(); idle.Vol = 0f; }
			if (high != null) { high.StopAll(); high.Vol = 0f; }
		}

		// ---- допоміжне ----
		private static AudioSource SrcOf(Transform root, int childIndex)
		{
			if (childIndex < 0 || root.childCount <= childIndex) return null;
			return root.GetChild(childIndex).GetComponent<AudioSource>();
		}

		// Разовий список усіх AudioSource на мопеді (до того, як додамо свої голоси) - щоб знайти штатний звук їзди.
		private static void DumpSources(Transform root)
		{
			try
			{
				AudioSource[] all = root.GetComponentsInChildren<AudioSource>(true);
				for (int i = 0; i < all.Length; i++)
				{
					AudioSource a = all[i];
					Transform p = a.transform;
					int idx = p.GetSiblingIndex();
					ModConsole.Print("[Planeta][Audio] src #" + i + " вузол=" + p.name + " (дитина " + idx + ") кліп=" + (a.clip != null ? a.clip.name : "-")
						+ " loop=" + a.loop + " vol=" + a.volume.ToString("0.00") + " play=" + a.isPlaying);
				}
			}
			catch (Exception e) { ModConsole.Print("[Planeta][Audio] DumpSources: " + e.Message); }
		}

		private static float FollowVol(AudioSource o)
		{
			return (o != null && o.gameObject.activeInHierarchy && o.isPlaying) ? o.volume * MasterGain : 0f;
		}

		private static float FollowPitch(AudioSource o)
		{
			return o != null ? Mathf.Clamp(o.pitch, 0.5f, 2f) : 1f;
		}

		private float ReadF(FieldInfo f, float fallback)
		{
			if (f == null || drive == null) return fallback;
			try { return Convert.ToSingle(f.GetValue(drive)); }
			catch { return fallback; }
		}

		private static string Name(FieldInfo f)
		{
			return f != null ? f.Name : "НЕМАЄ";
		}

		private static FieldInfo FindNumField(string name)
		{
			foreach (FieldInfo f in typeof(Drivetrain).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
				Type t = f.FieldType;
				if (t == typeof(float) || t == typeof(int) || t == typeof(double)) return f;
			}
			return null;
		}

		private static FieldInfo FindBoolField(params string[] names)
		{
			foreach (string name in names)
			{
				foreach (FieldInfo f in typeof(Drivetrain).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (f.FieldType == typeof(bool) && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
				}
			}
			return null;
		}
	}
}
