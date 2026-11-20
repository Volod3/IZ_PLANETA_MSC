using System;
using System.Reflection;
using MSCLoader;
using UnityEngine;

namespace Planeta
{
	// Запис/читання даних мода прямо у файл збереження гри (defaultES2File.txt, Easy Save 2).
	// Файл належить конкретному сейву, тож нова гра / інший слот = чиста позиція мопеда.
	// Усе через рефлексію: якщо ES2 не знайдено, Available = false і мод працює через запасний файл PLANETASave.save.
	internal static class GameSaveStore
	{
		private const string FileName = "defaultES2File.txt";
		private static bool inited;
		private static bool available;
		private static MethodInfo mSave, mLoad, mExists;

		public static bool Available
		{
			get { Init(); return available; }
		}

		private static void Init()
		{
			if (inited) return;
			inited = true;
			try
			{
				Type es2 = null;
				foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
				{
					try { es2 = a.GetType("ES2", false); } catch { es2 = null; }
					if (es2 != null) break;
				}
				if (es2 == null) { ModConsole.Print("[Planeta][Save] Тип ES2 не знайдено - працюю через запасний файл."); return; }
				foreach (MethodInfo m in es2.GetMethods(BindingFlags.Public | BindingFlags.Static))
				{
					ParameterInfo[] ps = m.GetParameters();
					if (m.Name == "Save" && m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType.IsGenericParameter && ps[1].ParameterType == typeof(string)) mSave = m;
					else if (m.Name == "Load" && m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string)) mLoad = m;
					else if (m.Name == "Exists" && !m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string)) mExists = m;
				}
				available = mSave != null && mLoad != null && mExists != null;
				ModConsole.Print("[Planeta][Save] ES2 " + (available ? "доступний - позиція пишеться в сейв гри." : "знайдено, але потрібних методів нема - запасний файл."));
			}
			catch (Exception e) { available = false; ModConsole.Print("[Planeta][Save] Init: " + e.Message); }
		}

		private static string Id(string tag)
		{
			return FileName + "?tag=" + tag;
		}

		public static bool Has(string tag)
		{
			if (!Available) return false;
			try { return (bool)mExists.Invoke(null, new object[] { Id(tag) }); }
			catch { return false; }
		}

		private static bool Put<T>(string tag, T value)
		{
			if (!Available) return false;
			try { mSave.MakeGenericMethod(typeof(T)).Invoke(null, new object[] { value, Id(tag) }); return true; }
			catch (Exception e) { ModConsole.Print("[Planeta][Save] Write " + tag + ": " + e.Message); return false; }
		}

		private static bool Get<T>(string tag, out T value)
		{
			value = default(T);
			if (!Available) return false;
			try
			{
				if (!Has(tag)) return false;
				value = (T)mLoad.MakeGenericMethod(typeof(T)).Invoke(null, new object[] { Id(tag) });
				return true;
			}
			catch (Exception e) { ModConsole.Print("[Planeta][Save] Read " + tag + ": " + e.Message); return false; }
		}

		public static bool Write(string tag, Vector3 v) { return Put<Vector3>(tag, v); }
		public static bool Write(string tag, float v) { return Put<float>(tag, v); }
		public static bool Read(string tag, out Vector3 v) { return Get<Vector3>(tag, out v); }
		public static bool Read(string tag, out float v) { return Get<float>(tag, out v); }
	}
}
