using UnityEditor;
using UnityEngine;

/// <summary>
/// §50: нормализует оси протезов при импорте.
///
/// Протезы экспортированы из Blender (Assets/ArtSource/Prosthetics/
/// hexlive_prosthetics.blend), и в FBX объект приезжает с поворотом −90° по X:
/// сама геометрия авторена вдоль своего +Y (сокет в 0, щиколотка в 1), но узел
/// разворачивает её в Z. Крепление протеза (FittedProstheticPoseFollower)
/// ставит модель ВДОЛЬ ОСИ КОНЕЧНОСТИ по +Y — и из-за повёрнутого узла
/// деревянная нога торчала горизонтально назад, а не вниз: игрок видел это как
/// «протез постоянно согнут».
///
/// Правится ЗДЕСЬ, а не углом в рантайме: кривая ось — свойство ассета, и
/// нормализовать её надо один раз на импорте (то же правило, что и для мебели,
/// см. .agents/skills/hexlive-furniture-authoring/SKILL.md). После этого модель
/// лежит вдоль +Y, а код не знает ни про какие компенсации.
///
/// ⚠️ Проверено, что виноват именно УЗЕЛ, а не меш: обнуление его поворота
/// разворачивает габарит с (0.44, 0.63, 1.20) на (0.44, 1.20, 0.63) — длинная
/// сторона встаёт по Y. Поэтому и лечится обнулением, а не докруткой на
/// подобранный угол.
///
/// Заметка на будущее: bakeAxisConversion тут НЕ помогает — он лишь меняет знак
/// поворота (270 ⇄ 90), потому что поворот сидит в самом FBX, а не в конвертации
/// осей Unity. Настоящее «сделать хорошо» — применить трансформ при экспорте из
/// Blender; этот постпроцессор чинит уже экспортированные файлы.
/// </summary>
public sealed class ProstheticImportPostprocessor : AssetPostprocessor
{
    private const string ProstheticsRoot = "Assets/HexLiveContent/Prosthetics";

    // Ниже этого угла узел считается уже нормальным и не трогается: правка
    // должна быть идемпотентной, иначе каждый реимпорт крутил бы модель заново.
    private const float StraightEnoughDegrees = 0.5f;

    private void OnPostprocessModel(GameObject root)
    {
        if (!assetPath.StartsWith(ProstheticsRoot) || root == null)
        {
            return;
        }

        foreach (Transform child in root.transform)
        {
            if (Quaternion.Angle(child.localRotation, Quaternion.identity) <= StraightEnoughDegrees)
            {
                continue;
            }

            child.localRotation = Quaternion.identity;
        }
    }
}
