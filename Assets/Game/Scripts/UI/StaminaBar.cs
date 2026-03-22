using UnityEngine;
using UnityEngine.UI;
 
/// <summary>
/// Minimal stamina bar UI. Reads from PlayerController.StaminaNormalised.
/// Works locally and in multiplayer (bind to owner's PlayerController).
/// </summary>
public class StaminaBar : MonoBehaviour
{
    [Tooltip("The Image component used as the fill bar (Image Type: Filled).")]
    public Image fillImage;
 
    [Tooltip("Colour when stamina is healthy.")]
    public Color fullColour      = Color.green;
 
    [Tooltip("Colour when stamina is critically low.")]
    public Color exhaustedColour = Color.red;
 
    [Tooltip("Threshold below which the bar turns red.")]
    [Range(0f, 1f)]
    public float lowThreshold = 0.2f;
 
    // Assigned at runtime — works for local or by the NetworkPlayer spawner
    private PlayerController _player;
 
    public void Bind(PlayerController player) => _player = player;
 
    private void Start()
    {
        var pc = GetComponentInParent<PlayerController>();
        Debug.Log(pc.name);
        Bind(pc);
    }
    
    private void Update()
    {
        if (_player == null || fillImage == null) return;
 
        float t = _player.StaminaNormalized;
        fillImage.fillAmount = t;
        fillImage.color      = Color.Lerp(exhaustedColour, fullColour,
                                    Mathf.InverseLerp(0f, lowThreshold, t));
    }
}