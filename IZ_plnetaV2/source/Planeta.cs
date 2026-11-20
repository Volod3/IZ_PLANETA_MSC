using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace Planeta
{
	public class Planeta : Mod
	{
		// ---- Константи, які може знадобитись підкрутити ----
		private const string MopedName = "Planeta Stok(349cm)";
		private const string PositionTag = "Planeta Stok(349cm)Transform"; // як було в оригіналі (щоб не ламати старі сейви)
		private const float MopedScale = 1.1f;
		private const float MopedYawOffset = 0f;   // якщо після телепорту мопед стоїть боком/задом - постав 90 / 180 / 270

		// ---- Збереження: пишемо прямо у файл збереження гри (defaultES2File.txt) ----
		// Той самий файл підміняють моди на кілька слотів, тож позиція мопеда сама належить конкретному сейву.
		// Окремий PLANETASave.save лишився тільки запасним варіантом (якщо ES2 недоступний).
		private const string SaveTagPos = "PlanetaMopedPos";
		private const string SaveTagRot = "PlanetaMopedRot";
		private const string SaveTagFuel = "PlanetaMopedFuel";
		private float resaveAt;   // повторний запис позиції через ~1.5 с після збереження гри

		// MOP: формат .mopconfig (взято з реального файлу гри)
		private const string MopRuleFile = "Planeta.mopconfig";
		private const string MopMinVerLine = "min_ver:2.8.3";
		private const string MopRulePrefix = "ignore:Planeta%20Stok(349cm)"; // ім'я об'єкта в URL-кодуванні
		private const string MopRuleLine = MopRulePrefix + " fullIgnore";

		private static readonly Vector3 StartPos = new Vector3(-13.7425f, -0.307f, 13.91f);
		private static readonly Quaternion StartRot = Quaternion.Euler(0f, 184.7f, 12.54f);

		private GameObject jonnez;
		private GameObject jonclone;      // мопед хоста (основний, зберігається в сейв)
		// ---- Мультиплеєр: мопеди інших гравців ----
		// Хост володіє основним мопедом (jonclone, зберігається в сейв). Кожен ІНШИЙ гравець з модом
		// (визначаємо через handshake Planeta_Hello / Planeta_Ack) отримує власний клон - їх може бути скільки завгодно.
		private class Peer
		{
			public ulong Id;
			public int Slot;
			public GameObject Go;
			public WreckAPI.NetVehicle Net;
			public int Fails;
			public Dictionary<string, float> Baseline = new Dictionary<string, float>();
			public Dictionary<string, float> Pending = new Dictionary<string, float>();
			public bool GearsTuned;
			public Vector3 Spot;
			public bool HasSpot;
			public float ReplaceAt;   // повторно виставити позицію (FSM збереження гри могла посунути клон)
			public float ReleaseAt;   // коли перевіряти, чи можна знімати kinematic (опитування до підтвердження володіння)
			public float NextOwnReq;  // коли наступний раз повторити RequestOwnership (лише для власного мопеда гостя)
			public bool Moved;        // справжню позицію вже виставлено (з мережі / телепортом) - у слот не повертаємо
			public Vector3 PlacedPos; // куди поставили при спавні (щоб відрізнити "посунули з мережі" від "стоїть у слоті")
		}

		private readonly List<Peer> peers = new List<Peer>();
		private const string PeerNamePrefix = "Planeta Stok(349cm) Player ";
		private const string PeerTagPrefix = "Planeta Stok(349cm)Player";
		private const int MaxPeers = 15;
		private const float GuestLift = 0.08f;   // наскільки підняти мопед над землею при спавні збоку
		private GameObject spawnIgnore;          // клон, який зараз ставимо (його колайдери не рахуємо перешкодою)
		private GameObject drivenNow;
		private bool drivenNetSent;      // true, якщо ми відправили SendEnterDrivingMode для drivenNow
		private float lastDriveErr;      // щоб помилка водіння не спамила консоль щокадру
		private int netFails;
		private float nextNetTry;
		private float nextHelloRetry;
		private float nextOwnSpawn;      // повторна спроба створити власний мопед гостя (MyId був 0 / спавн не вдався)
		private float nextSpawnRetry;    // повторні спроби створити мопеди для гравців, чий Hello вже був
		private const int RegFailLimit = 20; // спроб реєстрації NetVehicle (раз на 0.5 с), потім працюємо локально
		private bool mpInit;
		private WreckAPI.GameEvent helloEvent;     // "у тебе є Planeta?" (запит)
		private WreckAPI.GameEvent ackEvent;       // "так, є" (відповідь; на відповідь НЕ відповідаємо - без пінг-понгу)
		// Події WreckMP можуть прийти не з головного потоку: у callback лише кладемо в чергу, обробка в Mod_Update.
		private static Planeta instance;
		private static bool mpHooked;
		private readonly object mpLock = new object();
		private readonly Queue<ulong> qHello = new Queue<ulong>();
		private readonly Queue<ulong> qAck = new Queue<ulong>();
		private readonly Queue<ulong> qReady = new Queue<ulong>();
		private readonly Queue<ulong> qExit = new Queue<ulong>();
		private readonly Dictionary<ulong, bool> pendingSpawn = new Dictionary<ulong, bool>(); // Hello був, але мопед створити не вдалося - пробуємо ще
		private readonly Dictionary<ulong, bool> moddedPlayers = new Dictionary<ulong, bool>(); // хто підтвердив, що має Planeta
		private readonly Dictionary<ulong, int> helloTries = new Dictionary<ulong, int>();
		private bool loaded;
		private bool jonReleasePending;   // основний клон тримаємо kinematic, поки в мультиплеєрі не підтверджено локальне володіння
		private float jonReleaseDeadline; // хост/соло: після цього часу знімаємо kinematic, навіть якщо WreckMP так і не підтвердив володіння
		private float nextJonOwnReq;
		private float cloneAt;
		private int cloneTries;
		private const int CloneTryLimit = 30;  // скільки разів (раз на CloneDelay) пробувати знайти Jonnez, якщо MOP його ховає
		private float logAt;
		private const float CloneDelay = 3f; // секунд між спробами створити клона
		private float gearSyncUntil;  // до якого часу періодично підтягуємо КПП з оригіналу
		private float nextGearSync;
		private const float JonReleaseTimeout = 6f; // с: через скільки хост відпускає kinematic без підтвердження мережі
		private const float SyncWindow = 60f; // скільки секунд після старту підтягуємо зміни оригіналу від інших модів
		private SettingsCheckBox syncGearsBox;      // галочка: синхронізація КПП / двигуна (1000cc, КПП-моди)
		private SettingsCheckBox syncStabilityBox;  // галочка: синхронізація стабільності / фізики (моди стабільності)
		private bool snapMode, syncImmediate;
		private int stabChanged;
		private Dictionary<string, float> baseline = new Dictionary<string, float>();    // стан оригіналу в момент клонування
		private Dictionary<string, float> pendingVals = new Dictionary<string, float>(); // кандидати на копіювання (FSM)
		private bool gearsTuned;      // true, коли на оригіналі виявлено тюнінг КПП (інший мод)

		// ---- WreckMP ----
		private WreckAPI.NetVehicle netVeh;
		private bool inDrivingMode;
		private GameObject playerGo;
		private const string DriveVarValue = "Jonnez"; // значення глобальної змінної PlayerCurrentVehicle, коли сидиш на мопеді

		// Меші/текстури/звуки з бандла
		private Mesh mFenderFront, mFenderRear, mFrame, mHandle, mHeadlight, mHeadlightLight, mMotor,
			mRearFork, mRim, mSeat, mShocks, mColumn, mSteerFork, mThrottle, mTire;
		private Texture2D texParts, texPaint;
		private AudioClip idleClip, highClip;

		public override string ID => "Planeta";

		public override string Name => "Planeta beta";

		public override string Author => "Mihail_Kozlov";

		public override string Version => "1.0";

		// Нове API MSCLoader: замість override OnLoad/Update/OnSave - реєстрація через SetupFunction
		public override void ModSetup()
		{
			SetupFunction(Setup.OnNewGame, Mod_OnNewGame);
			SetupFunction(Setup.OnLoad, Mod_OnLoad);
			SetupFunction(Setup.Update, Mod_Update);
			SetupFunction(Setup.OnSave, Mod_OnSave);
			SetupFunction(Setup.ModSettings, Mod_Settings);
		}

		// ------------------------------------------------------------------
		//  Налаштування (кнопки в панелі MSCLoader)
		// ------------------------------------------------------------------
		private void Mod_Settings()
		{
			Settings.AddHeader("Синхронізація з оригінальним Jonnez");
			syncGearsBox = Settings.AddCheckBox("syncGears", "Sync gears / engine (1000cc, gearbox mods)", true);
			syncStabilityBox = Settings.AddCheckBox("syncStability", "Sync stability / physics (stability mods)", true);
			Settings.AddButton("Sync from original now", SyncNow);
			Settings.AddButton("Teleport moped to me", TeleportToPlayer);
			Settings.AddButton("Reset moped to house (start spot)", ResetToStart);
			// Settings.AddButton("MOP: create / restore ignore config", RestoreMopRule); // вимкнено разом з MOP-конфігом
		}

		// Нова гра: стара позиція мопеда (і в сейві гри, і в запасному файлі мода) не має переходити в новий сейв.
		// Запасний файл PLANETASave.save спільний для ВСІХ сейвів - без цього очищення мопед щоразу з'являвся
		// на старому місці, навіть у новій грі.
		private void Mod_OnNewGame()
		{
			ClearStoredPosition();
		}

		private void ClearStoredPosition()
		{
			try
			{
				SaveLoad.SerializeSaveFile<SaveData>(this, new SaveData(), "PLANETASave.save");
			}
			catch (Exception e) { ModConsole.Print("[Planeta] Не вдалося очистити запасний файл: " + e.Message); }
			try
			{
				if (GameSaveStore.Available)
				{
					// некоректна позиція = "мопеда в цьому сейві ще не було" (див. LoadSaved / IsSanePos)
					GameSaveStore.Write(SaveTagPos, new Vector3(0f, -9999f, 0f));
					GameSaveStore.Write(SaveTagRot, Vector3.zero);
					GameSaveStore.Write(SaveTagFuel, 5f);
				}
			}
			catch (Exception e) { ModConsole.Print("[Planeta] Не вдалося очистити позицію в сейві гри: " + e.Message); }
			ModConsole.Print("[Planeta] Нова гра: збережену позицію мопеда скинуто.");
		}

		private void Mod_OnLoad()
		{
			// Mod-об'єкт живе між перезавантаженнями гри: скидаємо стан, інакше після
			// "в меню -> знову грати" новий мопед ніколи не пройшов би налаштування.
			loaded = false;
			jonReleasePending = false;
			jonReleaseDeadline = 0f;
			nextJonOwnReq = 0f;
			jonclone = null;
			gearsTuned = false;
			netVeh = null;
			inDrivingMode = false;
			playerGo = null;
			instance = this;
			peers.Clear(); // об'єкти минулої сцени вже знищені разом зі сценою
			drivenNow = null;
			drivenNetSent = false;
			lastDriveErr = 0f;
			netFails = 0;
			nextNetTry = 0f;
			nextHelloRetry = 0f;
			nextOwnSpawn = 0f;
			nextSpawnRetry = 0f;
			gearSyncUntil = 0f;
			logAt = 0f;
			resaveAt = 0f;
			cloneTries = 0;
			spawnIgnore = null;
			mpInit = false;
			if (helloEvent != null) { try { helloEvent.Unregister(); } catch { } helloEvent = null; }
			if (ackEvent != null) { try { ackEvent.Unregister(); } catch { } ackEvent = null; }
			lock (mpLock) { qHello.Clear(); qAck.Clear(); qReady.Clear(); qExit.Clear(); }
			pendingSpawn.Clear();
			moddedPlayers.Clear();
			helloTries.Clear();
			baseline.Clear();
			pendingVals.Clear();

			// MOP-конфіг вимкнено: правило fullIgnore підвішувало меню. Щоб повернути - розкоментуй:
			// try { ModConsole.Print("[Planeta] MOP-конфіг: " + EnsureMopRule()); }
			// catch (Exception e) { ModConsole.Print("[Planeta] MOP-конфіг: " + e.Message); }

			// Клон створюємо одразу в OnLoad. Якщо мопед ще не знайдено (наприклад, MOP його вимкнув) -
			// Mod_Update повторює спробу кожні CloneDelay секунд (до CloneTryLimit разів).
			cloneAt = 0f;
			try { if (!CreateClone()) cloneAt = Time.time + CloneDelay; }
			catch (Exception e) { ModConsole.Print("[Planeta] Помилка створення клона: " + e); cloneAt = Time.time + CloneDelay; }
			LoadBundleAssets();
		}

		private bool CreateClone()
		{
			// MOP вимикає далекі об'єкти, тому звичайний GameObject.Find може повернути null.
			jonnez = FindByNameIncludingInactive("JONNEZ ES(Clone)");
			if (jonnez == null)
			{
				ModConsole.Print("[Planeta] Не знайдено JONNEZ ES(Clone) - мод не активується (спроба " + (cloneTries + 1) + ").");
				return false;
			}

			LogMoped("оригінал JONNEZ", jonnez, true, false);

			// Захист: якщо клон з таким іменем уже є (дубль), два мопеди в одному місці дають тряску/провали
			int dups = 0;
			foreach (GameObject o in Resources.FindObjectsOfTypeAll<GameObject>())
			{
				if (o != null && (o.name == MopedName || o.name.StartsWith(PeerNamePrefix)) && o.hideFlags == HideFlags.None && o.transform.parent == null)
				{
					UnityEngine.Object.Destroy(o);
					dups++;
				}
			}
			if (dups > 0) ModConsole.Print("[Planeta] Знайдено й видалено дублікатів клона: " + dups);

			GameObject created = UnityEngine.Object.Instantiate(jonnez, StartPos, StartRot) as GameObject;
			if (created == null) return false;
			jonclone = created;
			jonclone.name = MopedName;
			// MOP міг вимкнути оригінал (далеко від гравця) - клон народився б вимкненим і невидимим
			if (!jonclone.activeSelf) jonclone.SetActive(true);
			SyncStability(true, false);

			// Як в оригіналі: kinematic ставимо ПІСЛЯ Instantiate (інакше клон народжується вже kinematic)
			Rigidbody origRb = jonnez.GetComponent<Rigidbody>();
			if (origRb != null) origRb.isKinematic = true;

			Rigidbody rb = jonclone.GetComponent<Rigidbody>();
			if (rb != null) rb.isKinematic = true;

			// Як в оригіналі: тег позиції лише в першому FSM
			PlayMakerFSM[] fsms = jonclone.GetComponents<PlayMakerFSM>();
			if (fsms.Length > 0)
			{
				FsmString p = fsms[0].FsmVariables.FindFsmString("UniqueTagPosition");
				if (p != null) p.Value = PositionTag;
			}

			FsmFloat cap = FuelVar("MaxCapacity");
			if (cap != null) cap.Value = 5f;
			FsmFloat lvl = FuelVar("FuelLevel");
			if (lvl != null) lvl.Value = 5f;
			ModConsole.Print("[Planeta] Клон мопеда створено.");
			return true;
		}

		private void LoadBundleAssets()
		{
			AssetBundle b = LoadAssets.LoadBundle(this, "planeta5");
			if (b == null)
			{
				ModConsole.Print("[Planeta] Не вдалося завантажити бандл planeta5.");
				return;
			}
			mFenderFront = MeshOf(b, "jonnez_fender_front.obj");
			mFenderRear = MeshOf(b, "jonnez_fender_rear.obj");
			mFrame = MeshOf(b, "jonnez_frame.obj");
			mHandle = MeshOf(b, "jonnez_handle.obj");
			mHeadlight = MeshOf(b, "jonnez_headlight.obj");
			mHeadlightLight = MeshOf(b, "jonnez_headlight_light.obj");
			mMotor = MeshOf(b, "jonnez_motor.obj");
			mRearFork = MeshOf(b, "jonnez_rear_fork.obj");
			mRim = MeshOf(b, "jonnez_rim.obj");
			mSeat = MeshOf(b, "jonnez_seat.obj");
			mShocks = MeshOf(b, "jonnez_shocks_rear.obj");
			mColumn = MeshOf(b, "jonnez_steering_column.obj");
			mSteerFork = MeshOf(b, "jonnez_steering_fork.obj");
			mThrottle = MeshOf(b, "jonnez_throttle.obj");
			mTire = MeshOf(b, "jonnez_tire.obj");
			texParts = b.LoadAsset("ATLAS_MOTORPARTS.dds") as Texture2D;
			texPaint = b.LoadAsset("jonnez_paint.dds") as Texture2D;
			idleClip = b.LoadAsset("idle.wav") as AudioClip;
			highClip = b.LoadAsset("high.wav") as AudioClip;
			try { ModConsole.Print("[Planeta] Ассети бандла: " + string.Join(", ", b.GetAllAssetNames())); }
			catch (Exception ex) { ModConsole.Print("[Planeta] Список ассетів: " + ex.Message); }
			b.Unload(false);
		}

		private void Mod_Update()
		{
			if (jonclone == null)
			{
				if (cloneAt > 0f && Time.time >= cloneAt && Camera.main != null)
				{
					cloneAt = 0f;
					cloneTries++;
					bool ok = false;
					try { ok = CreateClone(); }
					catch (Exception e) { ModConsole.Print("[Planeta] Помилка створення клона: " + e); }
					// Раніше друга невдала спроба була останньою - тепер повторюємо, доки не вийде (або ліміт)
					if (!ok && jonclone == null && cloneTries < CloneTryLimit) cloneAt = Time.time + CloneDelay;
				}
				return;
			}

			// Як в оригіналі: масштаб тримаємо постійно (гра/MOP можуть його скинути)
			if (jonclone.transform.localScale.x != MopedScale)
				jonclone.transform.localScale = new Vector3(MopedScale, MopedScale, MopedScale);
			for (int pi = 0; pi < peers.Count; pi++)
			{
				GameObject pg = peers[pi].Go;
				if (pg != null && pg.transform.localScale.x != MopedScale)
					pg.transform.localScale = new Vector3(MopedScale, MopedScale, MopedScale);
			}

			if (!loaded && Camera.main != null)
			{
				loaded = true; // ставимо до налаштування: помилка не має повторюватись щокадру
				try { SetupMoped(); }
				catch (Exception e) { ModConsole.Print("[Planeta] Помилка налаштування: " + e); }
			}

			// Збій мультиплеєра не повинен зривати решту Mod_Update (збереження, синхронізацію) і спамити щокадру
			try { UpdateMultiplayer(); }
			catch (Exception e) { LogUpdateError("UpdateMultiplayer", e); }
			try { UpdateMpDriving(); }
			catch (Exception e) { LogUpdateError("UpdateMpDriving", e); }

			// Гра могла дописати свій сейв уже ПІСЛЯ нашого OnSave - дублюємо запис, коли вона закінчить
			if (resaveAt > 0f && Time.time >= resaveAt)
			{
				resaveAt = 0f;
				try { if (!MpOn() || AmHost()) WriteSave(false); }
				catch (Exception e) { ModConsole.Print("[Planeta] Повторний запис позиції: " + e.Message); }
			}

			if (logAt > 0f && Time.time >= logAt)
			{
				logAt = 0f;
				LogMoped("оригінал +3с", jonnez, false, true);
				LogMoped("клон +3с", jonclone, false, true);
				DiffMoped();
			}

			// Інший мод тюнінгу міг змінити КПП оригіналу вже після створення клона - підтягуємо
			if (gearSyncUntil > 0f && Time.time >= nextGearSync)
			{
				nextGearSync = Time.time + 2f;
				if (Time.time > gearSyncUntil) gearSyncUntil = 0f;
				else { SyncAll(); for (int gi = 0; gi < peers.Count; gi++) if (peers[gi].Go != null) WithClone(peers[gi].Go, SyncAll); }
			}

			// Основний клон у мультиплеєрі: kinematic, поки не підтверджено, що ним керуємо ми (див. OwnershipConfirmed).
			// ВАЖЛИВО: IsMultiplayerSession = true завжди, коли гру запущено через WreckMP (навіть наодинці). Раніше мопед
			// хоста лишався kinematic ("залізобетонний"), якщо WreckMP не підтверджував володіння (Owner != мій id) -
			// а це могло не статись ніколи. Тепер хост/соло гарантовано відпускає мопед за JonReleaseTimeout секунд.
			if (jonReleasePending)
			{
				bool release = OwnershipConfirmed(jonclone)
					|| (AmHost() && (Time.time >= jonReleaseDeadline || (netVeh == null && netFails >= RegFailLimit)));
				if (release)
				{
					jonReleasePending = false;
					Rigidbody rb = jonclone.GetComponent<Rigidbody>();
					if (rb != null) { rb.isKinematic = false; rb.WakeUp(); }
					if (!OwnershipConfirmed(jonclone)) ModConsole.Print("[Planeta][MP] Володіння не підтверджено мережею - мопед хоста відпущено локально.");
				}
				else if (OwnMoped() == jonclone && Time.time >= nextJonOwnReq && NetOf(jonclone) != null)
				{
					nextJonOwnReq = Time.time + 2f;
					RequestOwn(jonclone);
				}
			}
		}

		private float lastUpdErr;
		private void LogUpdateError(string where, Exception e)
		{
			if (Time.time < lastUpdErr) return;
			lastUpdErr = Time.time + 5f;
			ModConsole.Print("[Planeta] " + where + ": " + e);
		}

		// Виконує крок налаштування так, щоб збій одного кроку не зривав усе решту (раніше виняток у ApplyMeshes
		// лишав мопед kinematic назавжди і не запускав мультиплеєр).
		private void Step(string what, Action a)
		{
			try { a(); }
			catch (Exception e) { ModConsole.Print("[Planeta] Помилка кроку \"" + what + "\": " + e); }
		}

		private void SetupMoped()
		{
			Rigidbody rb = jonclone.GetComponent<Rigidbody>();
			if (rb != null) rb.isKinematic = true;

			// Позицію, яку міг виставити тег оригінального мода (сейв гри), запам'ятовуємо ДО перезапису
			Vector3 preloadPos = jonclone.transform.position;
			Quaternion preloadRot = jonclone.transform.rotation;

			SavedMoped saved = new SavedMoped();
			bool hasSaved = false;
			// Збережену позицію читаємо лише в одиночній грі або в хоста; гість отримує позицію мопеда хоста з мережі
			if (!MpOn() || AmHost())
			{
				try { hasSaved = LoadSaved(preloadPos, preloadRot, out saved); }
				catch (Exception e) { ModConsole.Print("[Planeta] Помилка читання сейву: " + e.Message); hasSaved = false; saved = new SavedMoped(); saved.Fuel = 5f; }
			}

			FsmFloat lvl = FuelVar("FuelLevel");
			if (hasSaved)
			{
				if (lvl != null) lvl.Value = saved.Fuel;
				jonclone.transform.position = saved.Pos;
				jonclone.transform.rotation = Quaternion.Euler(saved.Euler);
			}
			else
			{
				// У цьому сейві мопеда ще не було - спавн біля будинку, як в оригінальному моді
				if (lvl != null) lvl.Value = 5f;
				jonclone.transform.position = StartPos;
				jonclone.transform.rotation = StartRot;
			}

			Step("локальні трансформації", ApplyLocalTransforms);
			Step("меші", ApplyMeshes);
			Step("звук", ApplyAudio);
			Step("трансмісія", ApplyDrivetrain);
			Step("синхронізація", SyncAll);
			gearSyncUntil = Time.time + SyncWindow;
			nextGearSync = Time.time + 2f;

			jonclone.transform.localScale = new Vector3(MopedScale, MopedScale, MopedScale);

			// У мультиплеєрі НЕ знімаємо kinematic одразу: клон може бути чужим (мопед хоста на гостю) або ще не
			// зареєстрованим у WreckMP. Знімемо в Mod_Update, коли OwnershipConfirmed(). В одиночній грі - як раніше.
			if (MpOn()) { jonReleasePending = rb != null; jonReleaseDeadline = Time.time + JonReleaseTimeout; }
			else if (rb != null) rb.isKinematic = false;
			Rigidbody origRb = jonnez != null ? jonnez.GetComponent<Rigidbody>() : null;
			if (origRb != null) origRb.isKinematic = false; // як в оригіналі
			InitMultiplayer(); // handshake + мопеди інших гравців з модом (динамічно, у міру входу)
			LogMoped("клон", jonclone, true, false);
			logAt = Time.time + 3f;
			ModConsole.Print("[Planeta] Мопед завантажено.");
		}

		// ------------------------------------------------------------------
		//  WreckMP
		// ------------------------------------------------------------------
		// УВАГА: IsMultiplayerSession = "WreckMP-Present" у середовищі, тобто true завжди, коли гра запущена
		// через WreckMP (навіть якщо ти один у лобі). Тому "є інший гравець" визначаємо НЕ ним, а через handshake.
		private static bool MpOn()
		{
			try { return WreckAPI.WreckMPGlobals.IsMultiplayerSession; }
			catch { return false; }
		}

		private static ulong MyId()
		{
			try { return WreckAPI.WreckMPGlobals.UserID; }
			catch { return 0UL; }
		}

		private static bool AmHost()
		{
			try { return WreckAPI.WreckMPGlobals.IsHost; }
			catch { return true; }
		}

		private static ulong HostId()
		{
			try { return WreckAPI.WreckMPGlobals.HostID; }
			catch { return 0UL; }
		}

		private static Transform Pv(Transform root, string path)
		{
			Transform t = root.Find(path);
			if (t == null)
			{
				ModConsole.Print("[Planeta][MP] Нема піводу: " + path + " (беру корінь)");
				return root;
			}
			return t;
		}

		private WreckAPI.NetVehicle RegisterOne(GameObject g)
		{
			Transform root = g.transform;
			if (g.GetComponent("AxisCarController") == null || g.GetComponent<Rigidbody>() == null || g.GetComponent<Drivetrain>() == null)
				ModConsole.Print("[Planeta][MP] УВАГА: на " + g.name + " немає AxisCarController/Rigidbody/Drivetrain - WreckMP потребує їх для NetVehicle.");
			WreckAPI.NetVehicle nv = new WreckAPI.NetVehicle(root);
			const string SP = "LOD/Suspension/Steering/SteeringPivot/";
			WreckAPI.NetVehicleDriverPivots p = nv.DriverPivots;
			p.ThrottlePedal = Pv(root, "LOD/Crank");     // права нога
			p.BrakePedal = Pv(root, "LOD/Crank");        // ліва нога
			p.ClutchPedal = Pv(root, "LOD/Crank");       // педалі зчеплення нема - те саме, що гальмо
			p.SteeringWheel = Pv(root, SP + "Clutch");   // ліва рука
			p.GearStick = Pv(root, SP + "Throttle");     // права рука
			p.DriverParent = Pv(root, "MESH/seat");      // тіло
			return nv;
		}

		// Реєстрація повторюється, доки не вийде (сесія WreckMP може бути готова вже ПІСЛЯ SetupMoped).
		// Порядок: спершу основний мопед (хост), потім клони гравців.
		private void TryRegisterNetworkAll()
		{
			if (!MpOn() || jonclone == null) return;
			if (netVeh == null && netFails < RegFailLimit)
			{
				try
				{
					netVeh = RegisterOne(jonclone);
					ModConsole.Print("[Planeta][MP] Основний мопед зареєстровано в WreckMP.");
				}
				catch (Exception e) { netFails++; ModConsole.Print("[Planeta][MP] Помилка реєстрації основного мопеда (" + netFails + "/" + RegFailLimit + "): " + e.Message); }
			}
			// Клони інших гравців реєструємо незалежно від мопеда хоста (його збій не має блокувати решту)
			for (int i = 0; i < peers.Count; i++)
			{
				Peer p = peers[i];
				if (p.Net != null || p.Fails >= RegFailLimit || p.Go == null) continue;
				try
				{
					p.Net = RegisterOne(p.Go);
					ModConsole.Print("[Planeta][MP] Мопед гравця " + p.Id + " зареєстровано в WreckMP.");
					// свій мопед (гість) - беремо під своє керування, щоб він симулювався у нас
					if (p.Id == MyId()) RequestOwn(p.Go);
				}
				catch (Exception e) { p.Fails++; ModConsole.Print("[Planeta][MP] Помилка реєстрації мопеда гравця " + p.Id + " (" + p.Fails + "/" + RegFailLimit + "): " + e.Message); }
			}
		}

		private static void RequestOwn(GameObject g)
		{
			try
			{
				Rigidbody rb = g != null ? g.GetComponent<Rigidbody>() : null;
				if (rb != null) WreckAPI.NetRigidbodyManager.RequestOwnership(rb);
			}
			catch (Exception e) { ModConsole.Print("[Planeta][MP] RequestOwnership: " + e.Message); }
		}

		// Який із наших мопедів зараз під гравцем (null - гравець не на Planeta)
		private GameObject DrivenClone()
		{
			FsmString v = FsmVariables.GlobalVariables.FindFsmString("PlayerCurrentVehicle");
			if (v == null || v.Value != DriveVarValue) return null;
			if (playerGo == null) playerGo = GameObject.Find("PLAYER");
			if (playerGo == null || jonclone == null) return null;
			Vector3 pp = playerGo.transform.position;
			// усі мопеди мають одне ім'я в FSM - беремо найближчий до гравця (оригінальний Jonnez теж рахуємо)
			float best = jonnez != null ? (pp - jonnez.transform.position).sqrMagnitude : float.MaxValue;
			GameObject res = null;
			float d = (pp - jonclone.transform.position).sqrMagnitude;
			if (d < best) { best = d; res = jonclone; }
			for (int i = 0; i < peers.Count; i++)
			{
				if (peers[i].Go == null) continue;
				d = (pp - peers[i].Go.transform.position).sqrMagnitude;
				if (d < best) { best = d; res = peers[i].Go; }
			}
			return res;
		}

		private void UpdateMpDriving()
		{
			if (!MpOn()) return;
			GameObject cur = null;
			try
			{
				cur = DrivenClone();
				if (cur == drivenNow)
				{
					// сіли, поки мопед не був зареєстрований, а тепер реєстрація вдалась - повідомляємо мережу
					if (cur != null && !drivenNetSent)
					{
						WreckAPI.NetVehicle late = NetOf(cur);
						if (late != null && !(late.DriverSeatTaken && late.Driver != MyId())) { late.SendEnterDrivingMode(); drivenNetSent = true; }
					}
					return;
				}
				WreckAPI.NetVehicle nv = NetOf(cur);
				if (cur != null && nv == null && CanStillRegister(cur)) return; // цей клон ще не зареєстровано - повторимо наступного кадру
				// Exit шлемо лише якщо самі робили Enter (інакше можна випадково звільнити місце іншого водія)
				if (drivenNow != null && drivenNetSent)
				{
					WreckAPI.NetVehicle old = NetOf(drivenNow);
					if (old != null) old.SendExitDrivingMode();
				}
				drivenNow = cur;
				drivenNetSent = false;
				inDrivingMode = cur != null;
				if (nv != null)
				{
					if (nv.DriverSeatTaken && nv.Driver != MyId())
						ModConsole.Print("[Planeta][MP] Місце водія на цьому мопеді вже зайняте іншим гравцем - у мережі водіння не вмикаю.");
					else { nv.SendEnterDrivingMode(); drivenNetSent = true; }
				}
				ModConsole.Print("[Planeta][MP] Режим водіння: " + inDrivingMode + (cur != null && cur != jonclone && PeerOf(cur) != null ? " (мопед гравця " + PeerOf(cur).Id + ")" : ""));
			}
			catch (Exception e)
			{
				// Фіксуємо поточний стан, щоб помилка не повторювалась щокадру, і не спамимо консоль
				drivenNow = cur;
				drivenNetSent = false;
				inDrivingMode = cur != null;
				if (Time.time >= lastDriveErr)
				{
					lastDriveErr = Time.time + 5f;
					ModConsole.Print("[Planeta][MP] UpdateMpDriving: " + e.Message);
				}
			}
		}

		// ---- Локальні трансформації (раніше GameObject.Find -> тепер Transform.Find) ----
		private void ApplyLocalTransforms()
		{
			Vector3 one = Vector3.one;
			const string SP = "LOD/Suspension/Steering/SteeringPivot/";
			SetLocal("LOD/Suspension/ForkRear", new Vector3(-0.06316f, 0f, 0.02105f), new Vector3(0f, 70f, 0f), one);
			SetLocal("LOD/Suspension/ForkRear/Pivot/fork", new Vector3(0.08791f, 0f, -0.1099f), new Vector3(0f, 250f, 180f), one);
			SetLocal("LOD/Simulation/Exhaust", new Vector3(-0.0215264f, -0.2105284f, -0.3368421f), new Vector3(0f, 0f, 0f), one);
			SetLocal("LOD/Suspension/Steering", new Vector3(0.5229f, 0f, 0.4229f), null, one);
			SetLocal(SP + "Gauge", new Vector3(0.04807f, -0.13f, 0.1292f), new Vector3(38f, 0f, 0f), one);
			SetLocal(SP + "ForkFront", new Vector3(0f, 0f, 0.11474f), null, one);
			SetLocal(SP + "ForkFront/Pivot/TirePivot", new Vector3(0.06471f, -0.0062f, -0.56848f), null, one);
			SetLocal(SP + "Headlight", new Vector3(0f, -0.15071f, -0.0004f), new Vector3(35f, 0f, 270f), one);
			SetLocal("LOD/FuelFiller", new Vector3(-0.004f, 0.54f, 0.1607f), new Vector3(350f, 0f, 0f), one);
			SetLocal("LOD/Crank", new Vector3(-0.134f, 0.046f, -0.056f), null, new Vector3(1f, -1f, 1f));
			SetLocal(SP + "Throttle", new Vector3(0.288f, 0.0361579f, 0.234326f), new Vector3(0f, 355f, 9f), one);
			SetLocal("LOD/Dashboard/FuelTapPivot", new Vector3(0.0843f, 0.0513f, 0.3219361f), null, null);
			SetLocal("LOD/Dashboard/FuelTap", new Vector3(0.0843f, 0.0513f, 0.3219361f), null, null);
			SetLocal("LOD/Dashboard/Kickstart", new Vector3(0.127f, -0.085f, 0.167f), null, null);
		}

		private void SetLocal(string path, Vector3? pos, Vector3? euler, Vector3? scale)
		{
			Transform t = jonclone.transform.Find(path);
			if (t == null) { ModConsole.Print("[Planeta] Нема вузла: " + path); return; }
			if (scale.HasValue) t.localScale = scale.Value;
			if (pos.HasValue) t.localPosition = pos.Value;
			if (euler.HasValue) t.localEulerAngles = euler.Value;
		}

		// ---- Заміна мешів ----
		private void ApplyMeshes()
		{
			const string SP = "LOD/Suspension/Steering/SteeringPivot/";
			const string FF = SP + "ForkFront/Pivot/";
			const string RT = "LOD/Suspension/ForkRear/Pivot/TirePivot/Pivot/";

			SwapMesh(FF + "fender", mFenderFront, texParts);
			SwapMesh(SP + "Brake/Pivot/mesh", mHandle, null);
			SwapMesh(SP + "Clutch/Pivot/mesh", mHandle, null);
			SwapMesh(FF + "TirePivot/Pivot/tire_mesh/rim", mRim, null);
			SwapMesh(SP + "Column", mColumn, texParts);
			SwapMesh(FF + "fork", mSteerFork, null);
			SwapMesh(SP + "Throttle/Pivot/mesh", mThrottle, null);
			SwapMesh(FF + "TirePivot/Pivot/tire_mesh", mTire, null);
			SwapMesh("MESH/engine", mMotor, texParts);
			SwapMesh("MESH/frame", mFrame, texPaint);
			SwapMesh(RT + "tire_mesh", mTire, null);
			SwapMesh(RT + "rim", mRim, null);
			SwapMesh(SP + "Headlight", mHeadlight, null);
			SwapMesh(SP + "Headlight/Electricity/PowerON/BeamsShort/Light", mHeadlightLight, null);
			SwapMesh(SP + "Headlight/Light", mHeadlightLight, null);
			SwapMesh("LOD/Suspension/ForkRear/Pivot/fork", mRearFork, null);
			SwapMesh("MESH/shocks_rear", mShocks, null);
			SwapMesh("MESH/fender", mFenderRear, texParts);
			SwapMesh("MESH/seat", mSeat, texPaint);

			Transform chain = jonclone.transform.Find("LOD/Suspension/ForkRear/Pivot/chain");
			if (chain != null) chain.gameObject.SetActive(false);
		}

		private void SwapMesh(string path, Mesh mesh, Texture2D tex)
		{
			Transform t = jonclone.transform.Find(path);
			if (t == null) { ModConsole.Print("[Planeta] Нема вузла: " + path); return; }
			if (mesh != null)
			{
				MeshFilter mf = t.GetComponent<MeshFilter>();
				if (mf != null) mf.mesh = UnityEngine.Object.Instantiate(mesh) as Mesh; // копія, як в оригіналі (.mesh)
			}
			if (tex != null)
			{
				Renderer r = t.GetComponent<Renderer>();
				if (r != null) r.material.mainTexture = tex;
			}
		}

		// ---- Звук двигуна ----
		// Звуком керує PlanetaEngineAudio (див. PlanetaEngineAudio.cs): власний кросфейд на шві семпла,
		// мікс idle/high за обертами, згладжений pitch. Індекси 31/32 - як в оригіналі.
		private void ApplyAudio()
		{
			if (jonclone == null) return;
			PlanetaEngineAudio ea = jonclone.GetComponent<PlanetaEngineAudio>();
			if (ea == null) ea = jonclone.AddComponent<PlanetaEngineAudio>();
			ea.Setup(idleClip, highClip, 32, 31);
		}

		// Один рядок у консоль MSCLoader: передачі/КПП та/або положення коліс і шин (відносно пивота)
		private static void LogMoped(string label, GameObject go, bool gears, bool wheels)
		{
			try
			{
				if (go == null) return;
				StringBuilder sb = new StringBuilder("[Planeta] " + label + ": ");
				if (gears)
				{
					Drivetrain dt = go.GetComponent<Drivetrain>();
					if (dt != null)
					{
						foreach (FieldInfo f in typeof(Drivetrain).GetFields(BindingFlags.Public | BindingFlags.Instance))
						{
							object v = f.GetValue(dt);
							Array arr = v as Array;
							string n = f.Name.ToLower();
							if (arr != null && n.Contains("gear")) sb.Append(f.Name + "[" + arr.Length + "] ");
							else if (v != null && (n == "transmission" || n == "gear" || n == "maxtorque" || n == "minrpm" || n == "finaldriveratio"))
								sb.Append(f.Name + "=" + v + " ");
						}
					}
					sb.Append("scale=" + go.transform.lossyScale.x.ToString("0.##") + " ");
				}
				if (wheels)
				{
					float rootY = go.transform.position.y;
					foreach (WheelCollider wc in go.GetComponentsInChildren<WheelCollider>(true))
					{
						Vector3 c = wc.transform.TransformPoint(wc.center);
						float bottom = c.y - wc.radius * wc.transform.lossyScale.y - rootY;
						sb.Append("| " + wc.name + " r=" + wc.radius.ToString("0.###") + " susp=" + wc.suspensionDistance.ToString("0.###") + " низ=" + bottom.ToString("0.###") + " ");
					}
					foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
					{
						if (mr.name != "tire_mesh") continue;
						bool rear = false;
						for (Transform t = mr.transform; t != null && t != go.transform; t = t.parent)
							if (t.name == "ForkRear") { rear = true; break; }
						sb.Append("| шина " + (rear ? "задня" : "передня") + " низ=" + (mr.bounds.min.y - rootY).ToString("0.###") + " ");
					}
				}
				ModConsole.Print(sb.ToString());
			}
			catch (Exception e) { ModConsole.Print("[Planeta] LogMoped: " + e.Message); }
		}

		private void ApplyDrivetrain()
		{
			Drivetrain dt = jonclone.GetComponent<Drivetrain>();
			if (dt == null) return;
			dt.maxTorque = 15f;
			dt.minRPM = 4000f;
			dt.finalDriveRatio = 0.4f;
			dt.powerMultiplier = 1.5f;
			// Тип Transmissions недоступний при компіляції - ставимо значення 2 через рефлексію
			FieldInfo tf = typeof(Drivetrain).GetField("transmission");
			if (tf != null) tf.SetValue(dt, Enum.ToObject(tf.FieldType, 2));
			else ModConsole.Print("[Planeta] УВАГА: поле Drivetrain.transmission не знайдено - режим КПП не виставлено!");
		}

		// ------------------------------------------------------------------
		//  Синхронізація КПП з оригіналом + діагностика різниць
		// ------------------------------------------------------------------
		private static string RelPath(Transform t, Transform root)
		{
			if (t == root) return "<root>";
			string p = t.name;
			for (Transform x = t.parent; x != null && x != root; x = x.parent) p = x.name + "/" + p;
			return p;
		}

		private static string Dump(object v)
		{
			Array arr = v as Array;
			if (arr == null) return v == null ? "null" : v.ToString();
			StringBuilder sb = new StringBuilder("len=" + arr.Length + " [");
			for (int i = 0; i < arr.Length && i < 12; i++) sb.Append(arr.GetValue(i) + " ");
			return sb.Append("]").ToString();
		}

		// Копіює з оригіналу в клон лише все, що стосується передач: масиви/поля Drivetrain з "gear"
		// в назві та FSM-змінні з "gear" в назві (ліміт перемикання). Логіка мопеда не чіпається.
		private void SyncGears()
		{
			try
			{
				if (jonnez == null || jonclone == null) return;
				int changed = 0;
				bool gearsCopied = false;

				Drivetrain a = jonnez.GetComponent<Drivetrain>();
				Drivetrain b = jonclone.GetComponent<Drivetrain>();
				if (a != null && b != null)
				{
					foreach (FieldInfo f in typeof(Drivetrain).GetFields(BindingFlags.Public | BindingFlags.Instance))
					{
						string n = f.Name.ToLower();
						if (!n.Contains("gear") || n == "gear") continue; // поточну передачу не копіюємо
						object va = f.GetValue(a);
						object vb = f.GetValue(b);
						if (Dump(va) == Dump(vb)) continue;
						Array arr = va as Array;
						f.SetValue(b, arr != null ? arr.Clone() : va);
						ModConsole.Print("[Planeta] КПП: Drivetrain." + f.Name + " " + Dump(vb) + " -> " + Dump(va));
						changed++;
						if (arr != null) gearsCopied = true;
					}

					// Якщо інший мод змінив КПП оригіналу - значення Planeta (15 / 4000 / 0.4 / 1.5) під ванільні
					// передачі не підходять (мопед глохне на 1-й). Тоді беремо параметри двигуна з оригіналу.
					if (gearsCopied) gearsTuned = true;
					if (gearsTuned)
					{
						string[] names = { "maxTorque", "minRPM", "finalDriveRatio", "powerMultiplier", "engineInertia" };
						foreach (string nm in names)
						{
							FieldInfo tf2 = typeof(Drivetrain).GetField(nm);
							if (tf2 == null) continue;
							object ta = tf2.GetValue(a);
							object tb = tf2.GetValue(b);
							if (Dump(ta) == Dump(tb)) continue;
							tf2.SetValue(b, ta);
							ModConsole.Print("[Planeta] Двигун: " + nm + " " + Dump(tb) + " -> " + Dump(ta));
							changed++;
						}
					}
				}

				System.Collections.Generic.Dictionary<string, PlayMakerFSM> fm = new System.Collections.Generic.Dictionary<string, PlayMakerFSM>();
				foreach (PlayMakerFSM f in jonclone.GetComponentsInChildren<PlayMakerFSM>(true))
					fm[RelPath(f.transform, jonclone.transform) + "|" + f.FsmName] = f;
				foreach (PlayMakerFSM f in jonnez.GetComponentsInChildren<PlayMakerFSM>(true))
				{
					string key = RelPath(f.transform, jonnez.transform) + "|" + f.FsmName;
					PlayMakerFSM g;
					if (!fm.TryGetValue(key, out g)) continue;
					foreach (FsmFloat v in f.FsmVariables.FloatVariables)
					{
						if (!v.Name.ToLower().Contains("gear")) continue;
						FsmFloat o = g.FsmVariables.FindFsmFloat(v.Name);
						if (o != null && o.Value != v.Value) { ModConsole.Print("[Planeta] КПП: FSM " + key + " " + v.Name + " " + o.Value + " -> " + v.Value); o.Value = v.Value; changed++; }
					}
					foreach (FsmInt v in f.FsmVariables.IntVariables)
					{
						string vn = v.Name.ToLower();
						if (!vn.Contains("gear") || vn == "gear" || vn == "currentgear") continue; // поточну передачу не копіюємо
						FsmInt o = g.FsmVariables.FindFsmInt(v.Name);
						if (o != null && o.Value != v.Value) { ModConsole.Print("[Planeta] КПП: FSM " + key + " " + v.Name + " " + o.Value + " -> " + v.Value); o.Value = v.Value; changed++; }
					}
				}
				if (changed > 0) ModConsole.Print("[Planeta] КПП синхронізовано, змін: " + changed);
			}
			catch (Exception e) { ModConsole.Print("[Planeta] SyncGears: " + e.Message); }
		}

		// ------------------------------------------------------------------
		//  Галочки + синхронізація стабільності/фізики з оригінального Jonnez
		// ------------------------------------------------------------------
		private static bool On(SettingsCheckBox box)
		{
			try { return box == null || (bool)box.GetValue(); }
			catch { return true; }
		}

		private void SyncAll()
		{
			if (On(syncGearsBox)) SyncGears();
			if (On(syncStabilityBox)) SyncStability(false, false);
		}

		// Кнопка "Sync from original now": разова синхронізація (не їдь на оригінальному мопеді в цей момент)
		private void SyncNow()
		{
			if (jonclone == null || !loaded)
			{
				Notify("Мопед ще не завантажений. Зайди в гру і спробуй ще раз.");
				return;
			}
			bool g = On(syncGearsBox), st = On(syncStabilityBox);
			if (!g && !st) { Notify("Обидві галочки синхронізації вимкнені."); return; }
			// Усі клони (основний + мопеди гравців), без обмеження SyncWindow
			int n = 0, clones = 0;
			if (g) SyncGears();
			if (st) n += SyncStability(false, true);
			clones++;
			for (int i = 0; i < peers.Count; i++)
			{
				GameObject pg = peers[i].Go;
				if (pg == null) continue;
				clones++;
				int pn = 0;
				WithClone(pg, () => { if (g) SyncGears(); if (st) pn = SyncStability(false, true); });
				n += pn;
			}
			Notify("Синхронізовано з оригіналом (мопедів: " + clones + "). Змін фізики: " + n + ". Подробиці в консолі.");
		}

		// Порівнює параметр оригіналу з тим, яким він був у момент клонування (baseline).
		// Якщо інший мод змінив його ПІСЛЯ клонування - переносимо нове значення в клон.
		// Власні зміни Planeta та поточний стан клона (паливо тощо) не чіпаємо.
		private void Visit(string key, float orig, float cur, Action<float> set, bool needStable)
		{
			if (snapMode) { baseline[key] = orig; return; }
			float b;
			if (!baseline.TryGetValue(key, out b)) { baseline[key] = orig; return; }
			if (orig == b) { pendingVals.Remove(key); return; }
			if (needStable && !syncImmediate)
			{
				// динамічні значення FSM копіюємо, лише коли вони не змінились між двома перевірками
				float p;
				if (!pendingVals.TryGetValue(key, out p) || p != orig) { pendingVals[key] = orig; return; }
			}
			pendingVals.Remove(key);
			baseline[key] = orig;
			if (cur == orig) return;
			set(orig);
			stabChanged++;
			ModConsole.Print("[Planeta] Стабільність: " + key + " " + cur + " -> " + orig);
		}

		private static float CurveGet(WheelFrictionCurve cv, int i)
		{
			switch (i)
			{
				case 0: return cv.extremumSlip;
				case 1: return cv.extremumValue;
				case 2: return cv.asymptoteSlip;
				case 3: return cv.asymptoteValue;
				default: return cv.stiffness;
			}
		}

		private static WheelFrictionCurve CurveSet(WheelFrictionCurve cv, int i, float val)
		{
			switch (i)
			{
				case 0: cv.extremumSlip = val; break;
				case 1: cv.extremumValue = val; break;
				case 2: cv.asymptoteSlip = val; break;
				case 3: cv.asymptoteValue = val; break;
				default: cv.stiffness = val; break;
			}
			return cv;
		}

		// Динамічні FSM-змінні (стан, а не налаштування) - їх з оригіналу в клон не переносимо
		private static bool SkipFsmVar(string name)
		{
			string n = name.ToLower();
			string[] deny = { "fuel", "gear", "temp", "wear", "damage", "condition", "rpm", "speed", "vel", "pos", "angle", "time", "timer", "dist",
				"throttle", "brake", "steer", "clutch", "input" };
			foreach (string d in deny) if (n.Contains(d)) return true;
			return false;
		}

		// snapshot=true: лише запам'ятати стан оригіналу. Інакше - перенести зміни в клон.
		// Rigidbody (маса, drag, центр мас), WheelCollider (маса, підвіска, тертя) і FSM-float змінні.
		private int SyncStability(bool snapshot, bool immediate)
		{
			try
			{
				if (jonnez == null || jonclone == null) return 0;
				snapMode = snapshot;
				syncImmediate = immediate;
				stabChanged = 0;

				Rigidbody a = jonnez.GetComponent<Rigidbody>();
				Rigidbody b = jonclone.GetComponent<Rigidbody>();
				if (a != null && b != null)
				{
					Visit("rb.mass", a.mass, b.mass, v => b.mass = v, false);
					Visit("rb.drag", a.drag, b.drag, v => b.drag = v, false);
					Visit("rb.angularDrag", a.angularDrag, b.angularDrag, v => b.angularDrag = v, false);
					Vector3 ca = a.centerOfMass, cb = b.centerOfMass;
					Visit("rb.com.x", ca.x, cb.x, v => { Vector3 cm = b.centerOfMass; cm.x = v; b.centerOfMass = cm; }, false);
					Visit("rb.com.y", ca.y, cb.y, v => { Vector3 cm = b.centerOfMass; cm.y = v; b.centerOfMass = cm; }, false);
					Visit("rb.com.z", ca.z, cb.z, v => { Vector3 cm = b.centerOfMass; cm.z = v; b.centerOfMass = cm; }, false);
				}

				Dictionary<string, WheelCollider> wc = new Dictionary<string, WheelCollider>();
				foreach (WheelCollider w in jonclone.GetComponentsInChildren<WheelCollider>(true)) wc[RelPath(w.transform, jonclone.transform)] = w;
				foreach (WheelCollider w in jonnez.GetComponentsInChildren<WheelCollider>(true))
				{
					string path = RelPath(w.transform, jonnez.transform);
					WheelCollider c;
					if (!wc.TryGetValue(path, out c)) continue;
					string k = "wheel:" + path + ".";
					Visit(k + "mass", w.mass, c.mass, v => c.mass = v, false);
					Visit(k + "suspDist", w.suspensionDistance, c.suspensionDistance, v => c.suspensionDistance = v, false);
					JointSpring sa = w.suspensionSpring, sc = c.suspensionSpring;
					Visit(k + "spring", sa.spring, sc.spring, v => { JointSpring js = c.suspensionSpring; js.spring = v; c.suspensionSpring = js; }, false);
					Visit(k + "damper", sa.damper, sc.damper, v => { JointSpring js = c.suspensionSpring; js.damper = v; c.suspensionSpring = js; }, false);
					Visit(k + "target", sa.targetPosition, sc.targetPosition, v => { JointSpring js = c.suspensionSpring; js.targetPosition = v; c.suspensionSpring = js; }, false);
					for (int fi = 0; fi < 2; fi++)
					{
						bool fwd = fi == 0;
						WheelFrictionCurve fa = fwd ? w.forwardFriction : w.sidewaysFriction;
						WheelFrictionCurve fc = fwd ? c.forwardFriction : c.sidewaysFriction;
						for (int ci = 0; ci < 5; ci++)
						{
							int idx = ci;
							Visit(k + (fwd ? "fwd" : "side") + idx, CurveGet(fa, idx), CurveGet(fc, idx), v =>
							{
								WheelFrictionCurve cv = fwd ? c.forwardFriction : c.sidewaysFriction;
								cv = CurveSet(cv, idx, v);
								if (fwd) c.forwardFriction = cv; else c.sidewaysFriction = cv;
							}, false);
						}
					}
				}

				Dictionary<string, PlayMakerFSM> fm = new Dictionary<string, PlayMakerFSM>();
				foreach (PlayMakerFSM fs in jonclone.GetComponentsInChildren<PlayMakerFSM>(true))
					fm[RelPath(fs.transform, jonclone.transform) + "|" + fs.FsmName] = fs;
				foreach (PlayMakerFSM fs in jonnez.GetComponentsInChildren<PlayMakerFSM>(true))
				{
					string rel = RelPath(fs.transform, jonnez.transform);
					if (rel == "FuelTank" || rel.StartsWith("FuelTank/")) continue; // паливо в клона своє
					string key = rel + "|" + fs.FsmName;
					PlayMakerFSM g;
					if (!fm.TryGetValue(key, out g)) continue;
					foreach (FsmFloat val in fs.FsmVariables.FloatVariables)
					{
						if (SkipFsmVar(val.Name)) continue;
						FsmFloat o = g.FsmVariables.FindFsmFloat(val.Name);
						if (o == null) continue;
						Visit("fsm:" + key + ":" + val.Name, val.Value, o.Value, x => o.Value = x, true);
					}
				}

				if (!snapshot && stabChanged > 0) ModConsole.Print("[Planeta] Стабільність синхронізовано, змін: " + stabChanged);
				return stabChanged;
			}
			catch (Exception e) { ModConsole.Print("[Planeta] SyncStability: " + e.Message); return 0; }
		}

		// Лог усіх різниць між оригіналом і клоном (для діагностики)
		private void DiffMoped()
		{
			try
			{
				if (jonnez == null || jonclone == null) return;
				int n = 0;

				Drivetrain a = jonnez.GetComponent<Drivetrain>();
				Drivetrain b = jonclone.GetComponent<Drivetrain>();
				if (a != null && b != null)
				{
					foreach (FieldInfo f in typeof(Drivetrain).GetFields(BindingFlags.Public | BindingFlags.Instance))
					{
						string sa = Dump(f.GetValue(a)), sb = Dump(f.GetValue(b));
						if (sa != sb) ModConsole.Print("[Planeta][DIFF] Drivetrain." + f.Name + ": ориг=" + sa + " | клон=" + sb);
					}
				}

				Rigidbody ra = jonnez.GetComponent<Rigidbody>();
				Rigidbody rb = jonclone.GetComponent<Rigidbody>();
				if (ra != null && rb != null)
					ModConsole.Print("[Planeta][DIFF] RB mass " + ra.mass + "/" + rb.mass + " drag " + ra.drag + "/" + rb.drag +
						" com " + ra.centerOfMass + "/" + rb.centerOfMass + " interp " + ra.interpolation + "/" + rb.interpolation +
						" cd " + ra.collisionDetectionMode + "/" + rb.collisionDetectionMode);

				System.Collections.Generic.Dictionary<string, WheelCollider> wc = new System.Collections.Generic.Dictionary<string, WheelCollider>();
				foreach (WheelCollider w in jonclone.GetComponentsInChildren<WheelCollider>(true)) wc[RelPath(w.transform, jonclone.transform)] = w;
				foreach (WheelCollider w in jonnez.GetComponentsInChildren<WheelCollider>(true))
				{
					WheelCollider c;
					if (!wc.TryGetValue(RelPath(w.transform, jonnez.transform), out c)) { ModConsole.Print("[Planeta][DIFF] нема колеса в клоні: " + w.name); continue; }
					string so = w.radius + "|" + w.suspensionDistance + "|" + w.suspensionSpring.spring + "|" + w.suspensionSpring.damper + "|" + w.mass + "|" + w.center + "|" + w.transform.localPosition;
					string sc = c.radius + "|" + c.suspensionDistance + "|" + c.suspensionSpring.spring + "|" + c.suspensionSpring.damper + "|" + c.mass + "|" + c.center + "|" + c.transform.localPosition;
					if (so != sc) ModConsole.Print("[Planeta][DIFF] Wheel " + w.name + ": ориг=" + so + " | клон=" + sc);
				}

				System.Collections.Generic.Dictionary<string, PlayMakerFSM> fm = new System.Collections.Generic.Dictionary<string, PlayMakerFSM>();
				foreach (PlayMakerFSM f in jonclone.GetComponentsInChildren<PlayMakerFSM>(true))
					fm[RelPath(f.transform, jonclone.transform) + "|" + f.FsmName] = f;
				foreach (PlayMakerFSM f in jonnez.GetComponentsInChildren<PlayMakerFSM>(true))
				{
					string key = RelPath(f.transform, jonnez.transform) + "|" + f.FsmName;
					PlayMakerFSM g;
					if (!fm.TryGetValue(key, out g)) continue;
					foreach (FsmFloat v in f.FsmVariables.FloatVariables)
					{
						FsmFloat o = g.FsmVariables.FindFsmFloat(v.Name);
						if (o != null && v.Value != o.Value && n++ < 80) ModConsole.Print("[Planeta][DIFF] FSM " + key + " float " + v.Name + ": " + v.Value + " | " + o.Value);
					}
					foreach (FsmInt v in f.FsmVariables.IntVariables)
					{
						FsmInt o = g.FsmVariables.FindFsmInt(v.Name);
						if (o != null && v.Value != o.Value && n++ < 80) ModConsole.Print("[Planeta][DIFF] FSM " + key + " int " + v.Name + ": " + v.Value + " | " + o.Value);
					}
					foreach (FsmBool v in f.FsmVariables.BoolVariables)
					{
						FsmBool o = g.FsmVariables.FindFsmBool(v.Name);
						if (o != null && v.Value != o.Value && n++ < 80) ModConsole.Print("[Planeta][DIFF] FSM " + key + " bool " + v.Name + ": " + v.Value + " | " + o.Value);
					}
				}
				ModConsole.Print("[Planeta][DIFF] готово");
			}
			catch (Exception e) { ModConsole.Print("[Planeta] DiffMoped: " + e); }
		}

		// ------------------------------------------------------------------
		//  Телепорт до гравця (з перевіркою колізій)
		// ------------------------------------------------------------------
		// Кнопка телепорту: переносить ТІЛЬКИ свій мопед (хост - основний, гість - свій).
		// Мопед іншого гравця не чіпаємо.
		private void TeleportToPlayer()
		{
			GameObject own = OwnMoped();
			if (own == null || !loaded)
			{
				Notify("Мопед ще не завантажений. Зайди в гру і спробуй ще раз.");
				return;
			}
			// Якщо сидиш на мопеді - телепорт забрав би мопед з-під тебе
			GameObject driven = null;
			try { driven = DrivenClone(); } catch { }
			if (driven != null && driven == own)
			{
				Notify("Ти зараз на цьому мопеді. Зійди з нього і спробуй ще раз.");
				return;
			}
			WreckAPI.NetVehicle nv = NetOf(own);
			bool taken = false;
			try { taken = nv != null && nv.DriverSeatTaken && nv.Driver != MyId(); } catch { }
			if (taken)
			{
				Notify("На мопеді зараз їде інший гравець - телепорт неможливий.");
				return;
			}
			WithClone(own, TeleportToPlayerCore);
		}

		private void TeleportToPlayerCore()
		{
			if (jonclone == null || !loaded)
			{
				Notify("Мопед ще не завантажений. Зайди в гру і спробуй ще раз.");
				return;
			}
			GameObject player = GameObject.Find("PLAYER");
			if (player == null || Camera.main == null)
			{
				Notify("Гравця не знайдено (ти в меню?).");
				return;
			}

			Vector3 origin = player.transform.position;
			float yaw = Camera.main.transform.eulerAngles.y;
			Vector3 spot;
			if (!FindFreeSpot(player.transform, origin, yaw, out spot))
			{
				Notify("Поруч (до ~4.5 м) немає вільного місця для мопеда. Відійди на відкритіше місце.");
				return;
			}
			PlaceMoped(spot, yaw + MopedYawOffset);
			Peer tp = PeerOf(jonclone);
			if (tp != null) { tp.Moved = true; tp.ReplaceAt = 0f; } // справжня позиція - у слот не повертати
			NetAfterTeleport();
			Notify("Мопед телепортовано.");
		}

		// Кнопка "Reset moped to house": ставить свій мопед на стартове місце біля хати і одразу перезаписує сейв.
		// Рятує, коли в сейві лишилась стара/кривa позиція.
		private void ResetToStart()
		{
			GameObject own = OwnMoped();
			if (own == null || !loaded)
			{
				Notify("Мопед ще не завантажений. Зайди в гру і спробуй ще раз.");
				return;
			}
			GameObject driven = null;
			try { driven = DrivenClone(); } catch { }
			if (driven != null && driven == own)
			{
				Notify("Ти зараз на цьому мопеді. Зійди з нього і спробуй ще раз.");
				return;
			}
			WreckAPI.NetVehicle nv = NetOf(own);
			bool taken = false;
			try { taken = nv != null && nv.DriverSeatTaken && nv.Driver != MyId(); } catch { }
			if (taken)
			{
				Notify("На мопеді зараз їде інший гравець - скидання неможливе.");
				return;
			}
			WithClone(own, ResetToStartCore);
			if (own == jonclone && (!MpOn() || AmHost()))
			{
				try { WriteSave(false); } catch (Exception e) { ModConsole.Print("[Planeta] Запис після скидання: " + e.Message); }
			}
			Notify("Мопед повернуто на стартове місце біля хати.");
		}

		private void ResetToStartCore()
		{
			Rigidbody rb = jonclone.GetComponent<Rigidbody>();
			if (rb != null && !rb.isKinematic)
			{
				rb.velocity = Vector3.zero;
				rb.angularVelocity = Vector3.zero;
			}
			jonclone.transform.position = StartPos;
			jonclone.transform.rotation = StartRot;
			if (rb != null && !rb.isKinematic) rb.WakeUp();
			Peer tp = PeerOf(jonclone);
			if (tp != null) { tp.Moved = true; tp.ReplaceAt = 0f; }
			NetAfterTeleport();
		}

		private bool FindFreeSpot(Transform player, Vector3 origin, float yaw, out Vector3 spot)
		{
			float[] radii = { 2.0f, 2.8f, 3.6f, 4.5f };
			float[] angles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };
			foreach (float r in radii)
			{
				foreach (float a in angles)
				{
					Vector3 dir = Quaternion.Euler(0f, yaw + a, 0f) * Vector3.forward;
					Vector3 cand = origin + dir * r;
					Vector3 ground;
					if (!TryGround(cand, origin.y, player, out ground)) continue;
					if (Mathf.Abs(ground.y - origin.y) > 2.5f) continue;
					if (!IsClear(ground, yaw + MopedYawOffset, player)) continue;
					// чи немає стіни між гравцем і точкою
					if (SegmentBlocked(origin + Vector3.up * 1.0f, ground + Vector3.up * 0.8f, player)) continue;
					spot = ground;
					return true;
				}
			}
			spot = Vector3.zero;
			return false;
		}

		private bool TryGround(Vector3 xz, float refY, Transform player, out Vector3 ground)
		{
			Vector3 start = new Vector3(xz.x, refY + 1.5f, xz.z);
			RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, 5f);
			Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
			foreach (RaycastHit h in hits)
			{
				if (h.collider.isTrigger || IsOwn(h.collider, player)) continue;
				ground = h.point;
				return h.normal.y >= 0.75f; // не ставимо на круті схили
			}
			ground = Vector3.zero;
			return false;
		}

		private bool IsClear(Vector3 ground, float yaw, Transform player)
		{
			Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
			float[] along = { -0.8f, 0f, 0.8f };
			foreach (float a in along)
			{
				if (Blocked(ground + fwd * a + Vector3.up * 0.75f, 0.4f, player)) return false;
			}
			return !Blocked(ground + Vector3.up * 1.3f, 0.4f, player);
		}

		private bool Blocked(Vector3 center, float radius, Transform player)
		{
			foreach (Collider c in Physics.OverlapSphere(center, radius))
			{
				if (c.isTrigger || IsOwn(c, player)) continue;
				return true;
			}
			return false;
		}

		private bool SegmentBlocked(Vector3 from, Vector3 to, Transform player)
		{
			Vector3 d = to - from;
			RaycastHit[] hits = Physics.RaycastAll(from, d.normalized, d.magnitude);
			foreach (RaycastHit h in hits)
			{
				if (h.collider.isTrigger || IsOwn(h.collider, player)) continue;
				return true;
			}
			return false;
		}

		private bool IsOwn(Collider c, Transform player)
		{
			return c.transform.IsChildOf(jonclone.transform) || c.transform.IsChildOf(player);
		}

		private void PlaceMoped(Vector3 groundPos, float yaw)
		{
			// Не чіпаємо isKinematic: Jonnez керує своїм Rigidbody через власні FSM, і
			// перемикання kinematic ззовні могло ламати його стан (тряска).
			Rigidbody rb = jonclone.GetComponent<Rigidbody>();
			if (rb != null && !rb.isKinematic)
			{
				rb.velocity = Vector3.zero;
				rb.angularVelocity = Vector3.zero;
			}
			jonclone.transform.position = groundPos + Vector3.up * 0.5f; // тимчасово; точну висоту рахуємо нижче
			// Крутимо лише навколо вертикалі: нахил/стійку мопеда (як він стоїть на підніжці)
			// лишаємо як є.
			Vector3 fwd = jonclone.transform.forward;
			fwd.y = 0f;
			float curYaw = fwd.sqrMagnitude > 0.0001f
				? Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg
				: jonclone.transform.eulerAngles.y;
			jonclone.transform.rotation = Quaternion.AngleAxis(Mathf.DeltaAngle(curYaw, yaw), Vector3.up) * jonclone.transform.rotation;
			// Висота: ставимо мопед так, щоб найнижча точка його мешів була трохи над землею.
			float minY = float.MaxValue;
			foreach (MeshRenderer mr in jonclone.GetComponentsInChildren<MeshRenderer>())
			{
				if (!mr.enabled || !mr.gameObject.activeInHierarchy) continue;
				float y = mr.bounds.min.y;
				if (y < minY) minY = y;
			}
			float shift = groundPos.y + 0.05f - minY;
			if (minY < float.MaxValue && shift > -2f && shift < 2f)
				jonclone.transform.position += Vector3.up * shift;
			else
				jonclone.transform.position = groundPos + Vector3.up * 0.1f;
			if (rb != null && !rb.isKinematic) rb.WakeUp();
		}

		// ------------------------------------------------------------------
		//  Мопеди інших гравців (мультиплеєр) + допоміжне
		// ------------------------------------------------------------------

		private Peer PeerOf(GameObject g)
		{
			if (g == null) return null;
			for (int i = 0; i < peers.Count; i++) if (peers[i].Go == g) return peers[i];
			return null;
		}

		private Peer FindPeer(ulong id)
		{
			for (int i = 0; i < peers.Count; i++) if (peers[i].Id == id) return peers[i];
			return null;
		}

		// Виконує дію так, ніби "jonclone" - це target (стан синхронізації теж окремий).
		// Так уся наявна логіка (SyncGears/SyncStability/Apply*/PlaceMoped/Pv) працює для будь-якого мопеда без переписування.
		private void WithClone(GameObject target, Action a)
		{
			if (target == null || a == null) return;
			Peer p = PeerOf(target);
			if (p == null) { a(); return; }
			GameObject sc = jonclone;
			Dictionary<string, float> sb = baseline, sp = pendingVals;
			bool sg = gearsTuned;
			jonclone = p.Go; baseline = p.Baseline; pendingVals = p.Pending; gearsTuned = p.GearsTuned;
			try { a(); }
			finally { p.GearsTuned = gearsTuned; jonclone = sc; baseline = sb; pendingVals = sp; gearsTuned = sg; }
		}

		// Свій мопед: в одиночній грі і в хоста - основний; у гостя - його власний клон (null, поки не створено).
		private GameObject OwnMoped()
		{
			if (!MpOn() || AmHost()) return jonclone;
			Peer p = FindPeer(MyId());
			return p != null ? p.Go : null;
		}

		private WreckAPI.NetVehicle NetOf(GameObject g)
		{
			if (g == null) return null;
			if (g == jonclone) return netVeh;
			Peer p = PeerOf(g);
			return p != null ? p.Net : null;
		}

		// Локальне володіння підтверджено мережею: мопед зареєстровано і його власник - ми.
		private bool OwnershipConfirmed(GameObject g)
		{
			try
			{
				WreckAPI.NetVehicle nv = NetOf(g);
				ulong me = MyId();
				return nv != null && me != 0UL && nv.Owner == me;
			}
			catch { return false; }
		}

		// true, якщо реєстрація цього мопеда ще може вдатись (не вичерпано спроби)
		private bool CanStillRegister(GameObject g)
		{
			if (g == null) return false;
			if (g == jonclone) return netFails < RegFailLimit;
			Peer p = PeerOf(g);
			return p != null && p.Fails < RegFailLimit;
		}

		// ---- Handshake: "у тебе є Planeta?" ----
		// Hello = запит, Ack = відповідь. На Hello відповідаємо Ack ЗАВЖДИ (раніше відповідь була лише один раз
		// на гравця - після перезавантаження гри в одного з учасників він більше ніколи не бачив чужих мопедів).
		// На Ack не відповідаємо, тож пінг-понгу нема.
		private static void HookMpEvents()
		{
			if (mpHooked) return;
			mpHooked = true; // події WreckMP не мають відписки - підписуємось лише раз за життя гри
			try
			{
				WreckAPI.WreckMPGlobals.OnMemberReady(OnMemberReadyStatic);
				WreckAPI.WreckMPGlobals.OnMemberExit(OnMemberExitStatic);
			}
			catch (Exception e) { mpHooked = false; ModConsole.Print("[Planeta][MP] Не вдалося підписатись на події WreckMP: " + e.Message); }
		}

		private static void OnMemberReadyStatic(ulong id) { if (instance != null) lock (instance.mpLock) instance.qReady.Enqueue(id); }
		private static void OnMemberExitStatic(ulong id) { if (instance != null) lock (instance.mpLock) instance.qExit.Enqueue(id); }
		private static void OnHelloStatic(WreckAPI.GameEventReader r) { if (instance != null && r != null) lock (instance.mpLock) instance.qHello.Enqueue(r.Sender); }
		private static void OnAckStatic(WreckAPI.GameEventReader r) { if (instance != null && r != null) lock (instance.mpLock) instance.qAck.Enqueue(r.Sender); }

		private void InitMultiplayer()
		{
			if (!MpOn() || mpInit) return;
			try
			{
				instance = this;
				HookMpEvents();
				helloEvent = new WreckAPI.GameEvent("Planeta_Hello", OnHelloStatic, WreckAPI.GameScene.Game);
				ackEvent = new WreckAPI.GameEvent("Planeta_Ack", OnAckStatic, WreckAPI.GameScene.Game);
				mpInit = true;
				// Гість має власний мопед: створюємо одразу, щоб можна було їхати без очікування інших
				if (!AmHost() && MyId() != 0UL) SpawnPeer(MyId());
				TryRegisterNetworkAll();
				SendHelloAll();
				nextHelloRetry = Time.time + 10f;
				ModConsole.Print("[Planeta][MP] Multiplayer init: host=" + AmHost() + " мій id=" + MyId());
			}
			catch (Exception e) { ModConsole.Print("[Planeta][MP] InitMultiplayer: " + e); }
		}

		private void SendHello(ulong target)
		{
			try
			{
				if (helloEvent == null || target == 0UL || target == MyId()) return;
				helloEvent.SendEmpty(target, true);
			}
			catch (Exception e) { ModConsole.Print("[Planeta][MP] SendHello: " + e.Message); }
		}

		private void SendAck(ulong target)
		{
			try
			{
				if (ackEvent == null || target == 0UL || target == MyId()) return;
				ackEvent.SendEmpty(target, true);
			}
			catch (Exception e) { ModConsole.Print("[Planeta][MP] SendAck: " + e.Message); }
		}

		private void SendHelloAll()
		{
			Dictionary<ulong, WreckAPI.Player> pl = WreckAPI.WreckMPGlobals.Players;
			if (pl == null) return;
			foreach (ulong id in new List<ulong>(pl.Keys)) SendHello(id);
		}

		// Гравець id підтвердив, що має Planeta (Hello або Ack): створюємо йому мопед, якщо ще нема.
		private void OnPlayerHasMod(ulong id)
		{
			if (id == 0UL || id == MyId()) return;
			if (id != HostId()) // мопед хоста - це jonclone, для нього клон не потрібен
			{
				Peer sp = SpawnPeer(id);
				if (sp == null)
				{
					// Не вдалось створити мопед: НЕ позначаємо гравця як moddedPlayers і пробуємо ще (див. UpdateMultiplayer)
					pendingSpawn[id] = true;
					return;
				}
				pendingSpawn.Remove(id);
			}
			bool first = !moddedPlayers.ContainsKey(id);
			moddedPlayers[id] = true;
			if (first) ModConsole.Print("[Planeta][MP] Гравець " + id + " має Planeta.");
		}

		// Усе, що приходить з WreckMP, обробляємо тут, у головному потоці.
		private void ProcessMpQueue()
		{
			List<ulong> hello = null, ack = null, ready = null, exit = null;
			lock (mpLock)
			{
				if (qHello.Count > 0) { hello = new List<ulong>(qHello); qHello.Clear(); }
				if (qAck.Count > 0) { ack = new List<ulong>(qAck); qAck.Clear(); }
				if (qReady.Count > 0) { ready = new List<ulong>(qReady); qReady.Clear(); }
				if (qExit.Count > 0) { exit = new List<ulong>(qExit); qExit.Clear(); }
			}
			if (ready != null)
				foreach (ulong id in ready) SendHello(id); // новий гравець завантажився - питаємо, чи є в нього мод
			if (hello != null)
			{
				foreach (ulong id in hello)
				{
					if (id == MyId()) continue;
					SendAck(id);          // відповідаємо на КОЖЕН Hello (відповідь Ack не викликає нової відповіді)
					OnPlayerHasMod(id);
				}
			}
			if (ack != null)
				foreach (ulong id in ack) OnPlayerHasMod(id);
			if (exit != null)
			{
				foreach (ulong id in exit)
				{
					moddedPlayers.Remove(id);
					pendingSpawn.Remove(id);
					helloTries.Remove(id);
					// Мопед НЕ знищуємо: він уже зареєстрований у WreckMP (відписки в API нема), тож лишається
					// припаркованим; якщо гравець повернеться - підхопить цей самий клон.
					ModConsole.Print("[Planeta][MP] Гравець " + id + " вийшов (його мопед лишається на місці).");
				}
			}
		}

		private void UpdateMultiplayer()
		{
			if (!loaded) return;
			for (int i = 0; i < peers.Count; i++)
			{
				Peer p = peers[i];
				if (p.Go == null) continue;
				// Якщо мопедом уже керує мережа (чужий власник) або він зрушив зі слота - це справжня позиція, не чіпаємо
				if (!p.Moved && MpOn() && p.Net != null)
				{
					ulong ow = 0UL;
					try { ow = p.Net.Owner; } catch { }
					if (ow != 0UL && ow != MyId()) { p.Moved = true; p.ReplaceAt = 0f; }
				}
				if (p.ReplaceAt > 0f && Time.time >= p.ReplaceAt)
				{
					p.ReplaceAt = 0f;
					if (!p.Moved && (p.Go.transform.position - p.PlacedPos).sqrMagnitude > 0.5f * 0.5f) p.Moved = true;
					if (!p.Moved) PlacePeer(p);
				}
				if (p.ReleaseAt > 0f && Time.time >= p.ReleaseAt)
				{
					// Знімаємо kinematic ЛИШЕ коли мережа підтвердила, що власник - ми (Net зареєстрований, Owner == MyId).
					// Інакше (Net == null, чужий власник) тримаємо kinematic і перевіряємо знову.
					bool release = !MpOn() || OwnershipConfirmed(p.Go)
						|| (p.Id == MyId() && p.Net == null && p.Fails >= RegFailLimit); // WreckMP не прийняв - локальний режим
					if (release)
					{
						p.ReleaseAt = 0f;
						Rigidbody rb = p.Go.GetComponent<Rigidbody>();
						if (rb != null) { rb.isKinematic = false; rb.WakeUp(); }
					}
					else
					{
						p.ReleaseAt = Time.time + 0.5f;
						if (p.Id == MyId() && p.Net != null && Time.time >= p.NextOwnReq) { p.NextOwnReq = Time.time + 2f; RequestOwn(p.Go); }
					}
				}
			}
			if (!MpOn() || !mpInit) return;
			try { ProcessMpQueue(); }
			catch (Exception e) { ModConsole.Print("[Planeta][MP] ProcessMpQueue: " + e); }
			// Гість: власний мопед ще не створено (MyId був 0 на момент InitMultiplayer або спавн не вдався) - повторюємо
			if (!AmHost() && Time.time >= nextOwnSpawn && OwnMoped() == null)
			{
				nextOwnSpawn = Time.time + 1f;
				ulong me = MyId();
				if (me != 0UL) SpawnPeer(me);
			}
			// Гравці, чий Hello був, але мопед не створився
			if (pendingSpawn.Count > 0 && Time.time >= nextSpawnRetry)
			{
				nextSpawnRetry = Time.time + 1f;
				foreach (ulong pid in new List<ulong>(pendingSpawn.Keys))
				{
					if (SpawnPeer(pid) != null) { pendingSpawn.Remove(pid); moddedPlayers[pid] = true; ModConsole.Print("[Planeta][MP] Гравець " + pid + " має Planeta (мопед створено з повтору)."); }
				}
			}
			if (Time.time >= nextNetTry)
			{
				nextNetTry = Time.time + 0.5f;
				TryRegisterNetworkAll();
			}
			// Hello міг загубитись (інший гравець ще вантажив сцену) - ще кілька спроб для тих, хто не відповів
			if (Time.time >= nextHelloRetry)
			{
				nextHelloRetry = Time.time + 10f;
				try
				{
					Dictionary<ulong, WreckAPI.Player> pl = WreckAPI.WreckMPGlobals.Players;
					if (pl != null)
						foreach (ulong id in new List<ulong>(pl.Keys))
						{
							if (id == MyId() || moddedPlayers.ContainsKey(id)) continue;
							int n;
							helloTries.TryGetValue(id, out n);
							if (n >= 6) continue;
							helloTries[id] = n + 1;
							SendHello(id);
						}
				}
				catch (Exception e) { ModConsole.Print("[Planeta][MP] Hello retry: " + e.Message); }
			}
		}

		private void PlacePeer(Peer p)
		{
			WithClone(p.Go, () =>
			{
				if (p.HasSpot)
				{
					PlaceMoped(p.Spot, StartRot.eulerAngles.y);
					jonclone.transform.position += Vector3.up * GuestLift; // трохи над землею, щоб не загрузнути
				}
				else
				{
					// немає вільного місця - ставимо рядом зі стартом, кожен наступний трохи далі
					jonclone.transform.position = StartPos + Quaternion.Euler(0f, StartRot.eulerAngles.y + 90f, 0f) * Vector3.forward * (2.2f + 1.2f * p.Slot) + Vector3.up * 0.5f;
				}
			});
			if (p.Go != null) p.PlacedPos = p.Go.transform.position;
		}

		// Створює мопед для гравця id (або повертає вже існуючий - наприклад, після повторного входу).
		private Peer SpawnPeer(ulong id)
		{
			Peer created = null;
			try
			{
				Peer existing = FindPeer(id);
				if (existing != null)
				{
					if (existing.Go != null) return existing;
					peers.Remove(existing); // об'єкт знищено (зміна сцени/MOP) - створюємо заново
				}
				if (!loaded || jonnez == null || jonclone == null || id == 0UL) return null;
				if (peers.Count >= MaxPeers) { ModConsole.Print("[Planeta][MP] Досягнуто ліміту мопедів (" + MaxPeers + ")."); return null; }

				GameObject g = UnityEngine.Object.Instantiate(jonnez, StartPos, StartRot) as GameObject;
				if (g == null) return null;
				g.name = PeerNamePrefix + id; // ім'я однакове в усіх клієнтів (залежить лише від SteamID)
				if (!g.activeSelf) g.SetActive(true); // MOP міг вимкнути оригінал (далеко від гравця) - клон народився б вимкненим
				Rigidbody grb = g.GetComponent<Rigidbody>();
				if (grb != null) grb.isKinematic = true;

				Peer p = new Peer();
				p.Id = id;
				p.Go = g;
				p.Slot = peers.Count;
				peers.Add(p);
				created = p;

				Vector3 spot;
				spawnIgnore = g;
				try { p.HasSpot = FindSideSpot(out spot); }
				finally { spawnIgnore = null; }
				p.Spot = spot; // FindSideSpot сам виставляє Vector3.zero, якщо місця нема

				WithClone(g, () =>
				{
					SyncStability(true, false); // знімок оригіналу - як для основного клона
					PlayMakerFSM[] fsms = jonclone.GetComponents<PlayMakerFSM>();
					if (fsms.Length > 0)
					{
						FsmString tag = fsms[0].FsmVariables.FindFsmString("UniqueTagPosition");
						if (tag != null) tag.Value = PeerTagPrefix + id + "Transform"; // свій тег на кожного гравця
					}
					IsolatePeerSave(jonclone, id);
					FsmFloat cap = FuelVar("MaxCapacity");
					if (cap != null) cap.Value = 5f;
					FsmFloat lvl = FuelVar("FuelLevel");
					if (lvl != null) lvl.Value = 5f;

					// та сама "начинка", що й у основного клона (від того самого Jonnez); кожен крок окремо,
					// щоб збій одного не залишив напівналаштований мопед
					Step("peer: локальні трансформації", ApplyLocalTransforms);
					Step("peer: меші", ApplyMeshes);
					Step("peer: звук", ApplyAudio);
					Step("peer: трансмісія", ApplyDrivetrain);
					Step("peer: синхронізація", SyncAll);
					jonclone.transform.localScale = new Vector3(MopedScale, MopedScale, MopedScale);
				});
				PlacePeer(p);
				p.ReplaceAt = Time.time + 0.5f;  // ще раз після першого кадру (FSM могла посунути клон)
				p.ReleaseAt = Time.time + 1.0f;
				if (!p.HasSpot) ModConsole.Print("[Planeta] Не знайшов вільного місця збоку від старту для мопеда гравця " + id + " - ставлю поруч.");
				ModConsole.Print("[Planeta] Мопед гравця " + id + " створено: " + g.transform.position + " (всього чужих: " + peers.Count + ")");
				TryRegisterNetworkAll();
				return p;
			}
			catch (Exception e)
			{
				ModConsole.Print("[Planeta] Помилка створення мопеда гравця " + id + ": " + e);
				// без цього напівналаштований клон лишався в peers, і наступний SpawnPeer повертав його як "готовий"
				if (created != null)
				{
					peers.Remove(created);
					if (created.Go != null) UnityEngine.Object.Destroy(created.Go);
				}
				return null;
			}
		}

		// Клон Jonnez успадковує теги збереження оригіналу: без ізоляції він писав би свої дані в сейв хоста
		// (і перетирав би запис оригінального Jonnez). 1) усі UniqueTag* змінні отримують унікальні імена
		// гравця; 2) глобальні переходи по "SAVE"-подіях знімаються, тож FSM клона на збереження гри не реагує.
		private void IsolatePeerSave(GameObject g, ulong id)
		{
			try
			{
				int tags = 0, cut = 0;
				FieldInfo gf = typeof(Fsm).GetField("globalTransitions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				foreach (PlayMakerFSM f in g.GetComponentsInChildren<PlayMakerFSM>(true))
				{
					string where = RelPath(f.transform, g.transform) + "_" + f.FsmName;
					foreach (FsmString sv in f.FsmVariables.StringVariables)
					{
						if (sv.Name == null || !sv.Name.StartsWith("UniqueTag")) continue;
						if (sv.Value != null && sv.Value.StartsWith(PeerTagPrefix)) continue; // вже наш унікальний тег
						sv.Value = PeerTagPrefix + id + "_" + where + "_" + sv.Name;
						tags++;
					}
					FsmTransition[] gts = f.Fsm.GlobalTransitions;
					if (gts == null || gts.Length == 0 || gf == null) continue;
					List<FsmTransition> keep = new List<FsmTransition>();
					foreach (FsmTransition t in gts)
					{
						string en = t != null && t.FsmEvent != null ? t.FsmEvent.Name : null;
						if (!string.IsNullOrEmpty(en) && en.ToUpperInvariant().Contains("SAVE")) { cut++; continue; }
						keep.Add(t);
					}
					if (keep.Count != gts.Length) gf.SetValue(f.Fsm, keep.ToArray());
				}
				ModConsole.Print("[Planeta][MP] Ізоляція сейву клона " + id + ": тегів змінено " + tags + ", save-переходів знято " + cut);
			}
			catch (Exception e) { ModConsole.Print("[Planeta][MP] IsolatePeerSave: " + e.Message); }
		}

		// ---- Пошук вільного місця збоку від стартової позиції (не в домі, не в колайдерах) ----
		private static readonly string[] HouseTokens = { "building", "house", "home" };

		private bool IsHouseCollider(Collider c)
		{
			for (Transform t = c.transform; t != null; t = t.parent)
			{
				string n = t.name.ToLower();
				foreach (string tok in HouseTokens) if (n == tok || n.StartsWith(tok)) return true;
			}
			return false;
		}

		// колайдери, які не рахуємо перешкодою: тригери, самі мопеди Planeta, гравець
		private bool IgnoreForSpawn(Collider c)
		{
			if (c.isTrigger) return true;
			// клон, який зараз ставимо, не рахуємо; ІНШІ мопеди (в т.ч. основний) - це перешкоди, щоб гравці не накладались
			if (spawnIgnore != null && c.transform.IsChildOf(spawnIgnore.transform)) return true;
			if (playerGo == null) playerGo = GameObject.Find("PLAYER");
			if (playerGo != null && c.transform.IsChildOf(playerGo.transform)) return true;
			return false;
		}

		private bool SpawnGround(Vector3 xz, float refY, out Vector3 ground)
		{
			RaycastHit[] hits = Physics.RaycastAll(new Vector3(xz.x, refY + 1.5f, xz.z), Vector3.down, 4f);
			Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
			foreach (RaycastHit h in hits)
			{
				if (IgnoreForSpawn(h.collider)) continue;
				ground = h.point;
				return h.normal.y >= 0.85f && !IsHouseCollider(h.collider);
			}
			ground = Vector3.zero;
			return false;
		}

		// не під дахом / не в будинку: над точкою на 6 м немає жодного колайдера
		private bool IsOpenSky(Vector3 ground)
		{
			foreach (RaycastHit h in Physics.RaycastAll(ground + Vector3.up * 0.3f, Vector3.up, 6f))
				if (!IgnoreForSpawn(h.collider)) return false;
			return true;
		}

		private bool SpawnClear(Vector3 ground, float yaw)
		{
			Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
			float[] along = { -0.9f, -0.45f, 0f, 0.45f, 0.9f };
			foreach (float a in along)
			{
				Vector3 c = ground + fwd * a;
				if (SpawnBlocked(c + Vector3.up * 0.35f, 0.3f)) return false;
				if (SpawnBlocked(c + Vector3.up * 0.85f, 0.4f)) return false;
			}
			return !SpawnBlocked(ground + Vector3.up * 1.4f, 0.4f);
		}

		private bool SpawnBlocked(Vector3 center, float radius)
		{
			foreach (Collider c in Physics.OverlapSphere(center, radius))
			{
				if (IgnoreForSpawn(c)) continue;
				return true;
			}
			return false;
		}

		// Кільця навколо StartPos (мінімум 1.8 м - щоб не накладатись на мопед на старті).
		// Порядок кандидатів фіксований, тож у всіх гравців вибір збігається.
		private bool FindSideSpot(out Vector3 spot)
		{
			float yaw = StartRot.eulerAngles.y;
			float[] radii = { 1.8f, 2.4f, 3.0f, 3.8f, 4.6f, 5.5f, 6.5f };
			float[] angles = { 90f, -90f, 60f, -60f, 120f, -120f, 30f, -30f, 150f, -150f, 0f, 180f };
			foreach (float r in radii)
			{
				foreach (float a in angles)
				{
					Vector3 cand = StartPos + Quaternion.Euler(0f, yaw + a, 0f) * Vector3.forward * r;
					Vector3 ground;
					if (!SpawnGround(cand, StartPos.y, out ground)) continue;
					if (Mathf.Abs(ground.y - StartPos.y) > 1.5f) continue;
					if (!IsOpenSky(ground)) continue;
					if (!SpawnClear(ground, yaw)) continue;
					spot = ground;
					return true;
				}
			}
			spot = Vector3.zero;
			return false;
		}

		// ---- Мережа після телепорту свого мопеда ----
		// Після телепорту беремо мопед під своє керування через NetRigidbodyManager,
		// щоб мережа не повернула його на стару позицію.
		private void NetAfterTeleport()
		{
			try
			{
				if (!MpOn() || jonclone == null) return;
				RequestOwn(jonclone);
			}
			catch (Exception e) { ModConsole.Print("[Planeta][MP] NetAfterTeleport: " + e.Message); }
		}

		// ------------------------------------------------------------------
		//  MOP: конфіг ігнорування (вимкнено, лишено для швидкого повернення)
		// ------------------------------------------------------------------
		private static string MopConfigDir
		{
			get
			{
				string game = Directory.GetParent(Application.dataPath).FullName;
				// <гра>/Mods/Config/Mod Settings/MOP
				return Path.Combine(Path.Combine(Path.Combine(Path.Combine(game, "Mods"), "Config"), "Mod Settings"), "MOP");
			}
		}

		/// <summary>Створює або відновлює правило. Повертає "created" / "restored" / "ok".</summary>
		private string EnsureMopRule()
		{
			string dir = MopConfigDir;
			Directory.CreateDirectory(dir);
			string file = Path.Combine(dir, MopRuleFile);

			if (!File.Exists(file))
			{
				File.WriteAllText(file, MopMinVerLine + Environment.NewLine + MopRuleLine + Environment.NewLine);
				return "created";
			}

			string[] lines = File.ReadAllLines(file);
			bool hasMinVer = false, hasRule = false;
			foreach (string raw in lines)
			{
				string line = raw.Trim();
				if (line.StartsWith("min_ver:")) hasMinVer = true;
				if (line == MopRuleLine) hasRule = true;
			}
			if (hasMinVer && hasRule) return "ok";

			// відновлення: збираємо файл наново, зберігаючи чужі рядки
			System.Collections.Generic.List<string> outLines = new System.Collections.Generic.List<string>();
			if (!hasMinVer) outLines.Add(MopMinVerLine);
			foreach (string raw in lines)
			{
				string line = raw.Trim();
				if (line.Length == 0) continue;
				if (line.StartsWith(MopRulePrefix)) continue; // зіпсований/застарілий варіант нашого правила
				outLines.Add(line);
			}
			outLines.Add(MopRuleLine);
			File.WriteAllLines(file, outLines.ToArray());
			return "restored";
		}

		private void RestoreMopRule()
		{
			try
			{
				string res = EnsureMopRule();
				string path = Path.Combine(MopConfigDir, MopRuleFile);
				if (res == "ok") Notify("Конфіг MOP вже в порядку:\n" + path);
				else Notify("Конфіг MOP " + (res == "created" ? "створено" : "відновлено") +
					":\n" + path + "\nПерезапусти гру, щоб MOP його підхопив.");
			}
			catch (Exception e)
			{
				Notify("Не вдалося записати конфіг MOP: " + e.Message);
			}
		}

		// ------------------------------------------------------------------
		//  Збереження
		// ------------------------------------------------------------------
		private struct SavedMoped
		{
			public Vector3 Pos;
			public Vector3 Euler;
			public float Fuel;
		}

		// Адекватна позиція світу (відсікає NaN, нескінченність і мопед, що провалився під карту)
		private static bool IsSanePos(Vector3 p)
		{
			return Mathf.Abs(p.x) < 5000f && Mathf.Abs(p.z) < 5000f && p.y > -50f && p.y < 500f;
		}

		// true - у цьому сейві є збережений мопед (s заповнено); false - спавнимо біля будинку.
		private bool LoadSaved(Vector3 preloadPos, Quaternion preloadRot, out SavedMoped s)
		{
			s = new SavedMoped();
			s.Fuel = 5f;

			if (GameSaveStore.Available)
			{
				// 1) файл збереження гри: окремо для кожного сейву / слота
				Vector3 p, r;
				if (GameSaveStore.Read(SaveTagPos, out p) && GameSaveStore.Read(SaveTagRot, out r))
				{
					if (!IsSanePos(p))
					{
						ModConsole.Print("[Planeta] Збережена позиція некоректна (" + p + ") - ставлю біля будинку.");
						return false;
					}
					s.Pos = p;
					s.Euler = r;
					float f;
					if (GameSaveStore.Read(SaveTagFuel, out f) && f >= 0f && f <= 1000f) s.Fuel = f;
					ModConsole.Print("[Planeta] Позицію з сейву гри завантажено: " + p);
					return true;
				}

				// 2) сейв зі старою версією: тег оригінального мода вже міг поставити клон на місце
				if ((preloadPos - StartPos).sqrMagnitude > 0.25f && IsSanePos(preloadPos))
				{
					s.Pos = preloadPos;
					s.Euler = preloadRot.eulerAngles;
					ModConsole.Print("[Planeta] Позицію взято зі старого сейву гри: " + preloadPos);
					return true;
				}
				return false;   // у цьому сейві мопеда ще не було
			}

			// 3) ES2 недоступний: запасний файл мода (він спільний для всіх слотів)
			SaveData data = SaveLoad.DeserializeSaveFile<SaveData>(this, "PLANETASave.save");
			if (data != null && data.save != null && data.save.Count > 0 && IsSanePos(data.save[0].pos))
			{
				SaveDataList d = data.save[0];
				s.Pos = d.pos;
				s.Euler = new Vector3(d.rotX, d.rotY, d.rotZ);
				s.Fuel = d.fuel;
				ModConsole.Print("[Planeta] ES2 недоступний - позицію взято із запасного файлу.");
				return true;
			}
			return false;
		}

		private void Mod_OnSave()
		{
			if (jonclone == null || !loaded) return; // !loaded: SetupMoped ще не виставив збережену позицію - не перетираємо сейв стартовою
			// у мультиплеєрі сейв мопеда пише лише хост (гість свій сейв не чіпає)
			if (MpOn() && !AmHost()) return;

			WriteSave(true);
			resaveAt = Time.time + 1.5f;
		}

		private void WriteSave(bool announce)
		{
			if (jonclone == null || !loaded) return;

			FsmFloat lvl = FuelVar("FuelLevel");
			float fuel = lvl != null ? lvl.Value : 5f;
			Vector3 pos = jonclone.transform.position;
			Vector3 euler = jonclone.transform.rotation.eulerAngles;
			if (!IsSanePos(pos))
			{
				// мопед провалився / улетів - наступного разу він з'явиться біля будинку
				if (announce) ModConsole.Print("[Planeta] Позиція мопеда некоректна (" + pos + ") - у сейв пишу стартову.");
				pos = StartPos;
				euler = StartRot.eulerAngles;
			}

			if (GameSaveStore.Available)
			{
				bool ok = GameSaveStore.Write(SaveTagPos, pos)
					&& GameSaveStore.Write(SaveTagRot, euler)
					&& GameSaveStore.Write(SaveTagFuel, fuel);
				if (ok && GameSaveStore.Has(SaveTagPos))
				{
					if (announce) ModConsole.Print("[Planeta] Позицію збережено в сейв гри: " + pos);
					return;
				}
				ModConsole.Print("[Planeta] Не вдалося записати позицію в сейв гри - пишу у запасний файл.");
			}

			SaveDataList item = new SaveDataList
			{
				fuel = fuel,
				pos = pos,
				rotX = euler.x,
				rotY = euler.y,
				rotZ = euler.z
			};
			SaveData data = new SaveData();
			data.save.Add(item);
			SaveLoad.SerializeSaveFile<SaveData>(this, data, "PLANETASave.save");
		}

		// ------------------------------------------------------------------
		//  Допоміжне
		// ------------------------------------------------------------------
		private FsmFloat FuelVar(string varName)
		{
			if (jonclone == null) return null;
			Transform t = jonclone.transform.Find("FuelTank");
			if (t == null) return null;
			PlayMakerFSM fsm = t.GetComponent<PlayMakerFSM>();
			// FindFsmFloat повертає null, якщо змінної нема (GetFsmFloat міг віддати "пусту" - і в сейв писалось паливо 0)
			return fsm == null ? null : fsm.FsmVariables.FindFsmFloat(varName);
		}

		private static Mesh MeshOf(AssetBundle b, string asset)
		{
			GameObject g = b.LoadAsset(asset) as GameObject;
			if (g == null) { ModConsole.Print("[Planeta] Нема ассета: " + asset); return null; }
			MeshFilter mf = null;
			if (g.transform.childCount > 0) mf = g.transform.GetChild(0).GetComponent<MeshFilter>();
			if (mf == null)
			{
				MeshFilter[] all = g.GetComponentsInChildren<MeshFilter>(true);
				if (all.Length > 0) mf = all[0];
			}
			return mf != null ? mf.sharedMesh : null;
		}

		private static GameObject FindByNameIncludingInactive(string objName)
		{
			GameObject g = GameObject.Find(objName);
			if (g != null) return g;
			foreach (GameObject o in Resources.FindObjectsOfTypeAll<GameObject>())
			{
				if (o.name == objName && o.hideFlags == HideFlags.None) return o;
			}
			return null;
		}

		private static void Notify(string msg)
		{
			ModConsole.Print("[Planeta] " + msg.Replace("\n", " "));
			ModUI.ShowMessage(msg, "Planeta");
		}
	}
}
