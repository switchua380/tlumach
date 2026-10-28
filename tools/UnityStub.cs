namespace UnityEngine
{
    public class Object { }
    public class ScriptableObject : Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class GameObject : Object { }
    public class Material : Object { }
    public class Shader : Object { }
    public class Texture : Object { }
    public class Texture2D : Texture { }
    public class Font : Object { }
    public class Sprite : Object { }
    public class TextAsset : Object { }
    public struct Color { public float r, g, b, a; }
    public struct Color32 { public byte r, g, b, a; }
    public struct Vector2 { public float x, y; }
    public struct Vector3 { public float x, y, z; }
    public struct Vector4 { public float x, y, z, w; }
    public struct Rect { public float x, y, width, height; }
    public struct Matrix4x4 { public float m00; }
    public sealed class SerializeField : System.Attribute { }
    public sealed class SerializeReference : System.Attribute { }
}
