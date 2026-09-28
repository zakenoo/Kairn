using System.Text.Json.Serialization;
using Kairn.Models;

namespace Kairn.Services;

/// <summary>
/// Lecture et écriture de data.json et settings.json préparées à la compilation : le premier chargement
/// n'a plus à tout découvrir par réflexion (~15 ms de gagnés au démarrage, sur ~75).
/// </summary>
[JsonSerializable(typeof(AppData))]
[JsonSerializable(typeof(AppSettings))]
internal partial class KairnJsonContext : JsonSerializerContext;
