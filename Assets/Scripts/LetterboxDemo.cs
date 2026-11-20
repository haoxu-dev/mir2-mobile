using UnityEngine;

// 运行时自检画面：验证「横屏 + 4:3 居中 + 两侧黑边」P0 基线
// 用 RuntimeInitializeOnLoadMethod 免去搭场景，构建后必定生效
public class LetterboxDemo : MonoBehaviour
{
    Texture2D _tex;
    float _fps;
    int _frames;
    float _t;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        // ★ 场景门控（2026-10-24）：本工程多个脚本都用 AfterSceneLoad 自举，Unity 不保证
        //   执行顺序；不加门控时它们会互相抢渲染/互相 Destroy。按当前场景名门控后，
        //   每个包里只会活一个引导，顺序无关。
        string scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!string.IsNullOrEmpty(scn) && scn != "Main")
        {
            Debug.Log("[DEMO] boot skipped, active scene = '" + scn + "'");
            return;
        }
        var go = new GameObject("LetterboxDemo");
        DontDestroyOnLoad(go);
        go.AddComponent<LetterboxDemo>();
    }

    void Start()
    {
        // 强制横屏
        Screen.orientation = ScreenOrientation.LandscapeLeft;
        Screen.autorotateToLandscapeLeft = true;
        Screen.autorotateToLandscapeRight = true;
        Screen.autorotateToPortrait = false;
        Screen.autorotateToPortraitUpsideDown = false;

        _tex = Resources.Load<Texture2D>("test_image");
        Debug.Log("[DEMO] START screen=" + Screen.width + "x" + Screen.height
                  + " dpi=" + Screen.dpi + " tex=" + (_tex != null ? _tex.width + "x" + _tex.height : "NULL"));
    }

    void Update()
    {
        _frames++;
        _t += Time.unscaledDeltaTime;
        if (_t >= 0.5f) { _fps = _frames / _t; _frames = 0; _t = 0f; }
    }

    void OnGUI()
    {
        int sw = Screen.width, sh = Screen.height;

        // 两侧黑边（其实是整个背景，中间挖出 4:3 区）
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0, 0, sw, sh), Texture2D.whiteTexture);
        GUI.color = Color.white;

        // 计算 4:3 居中视口
        const float TARGET = 4f / 3f;
        float vw = sw, vh = sw / TARGET;
        if (vh > sh) { vh = sh; vw = sh * TARGET; }
        float vx = (sw - vw) * 0.5f;
        float vy = (sh - vh) * 0.5f;
        var vp = new Rect(vx, vy, vw, vh);

        if (_tex != null)
        {
            // 按 4:3 视口铺满贴图（保持比例，不拉伸）
            GUI.DrawTexture(vp, _tex, ScaleMode.ScaleToFit, false);
        }
        else
        {
            GUI.color = new Color(0.1f, 0.12f, 0.18f, 1f);
            GUI.DrawTexture(vp, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        // 4:3 视口描边
        DrawBorder(vp, 2, Color.yellow);

        // 两侧黑边区域描边（演示虚拟按键可用区）
        var leftBar = new Rect(0, 0, vx, sh);
        var rightBar = new Rect(vx + vw, 0, sw - (vx + vw), sh);
        DrawBorder(leftBar, 1, new Color(0f, 1f, 0f, 0.55f));
        DrawBorder(rightBar, 1, new Color(0f, 1f, 0f, 0.55f));

        // 信息叠加
        var st = new GUIStyle(GUI.skin.label);
        st.fontSize = Mathf.Max(14, sh / 45);
        st.normal.textColor = Color.white;

        string barW = Mathf.RoundToInt(vx) + "px";
        GUI.Label(new Rect(12, 8, sw, st.fontSize * 2),
            "PROBE  " + sw + "x" + sh + "  FPS " + Mathf.RoundToInt(_fps), st);
        GUI.Label(new Rect(12, 8 + st.fontSize * 1.4f, sw, st.fontSize * 2),
            "4:3 viewport " + Mathf.RoundToInt(vw) + "x" + Mathf.RoundToInt(vh)
            + "   side bars " + barW + " each", st);

        if (_tex == null)
        {
            var r = new GUIStyle(st); r.normal.textColor = Color.red;
            GUI.Label(new Rect(12, 8 + st.fontSize * 2.8f, sw, st.fontSize * 2),
                "texture NULL", r);
        }
    }

    static void DrawBorder(Rect r, int t, Color c)
    {
        GUI.color = c;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }
}
