using System;
using Unity.VisualScripting;
using UnityEngine;

public class PlayerMovement : MonoBehaviour {
	
	[Header("General")]
	[SerializeField] private LayerMask solidsLayerMask;
	[SerializeField] private BoxCollider2D box;
	[SerializeField] private Animator bodyAnimator;
	[SerializeField] private SpriteRenderer bodyRenderer;

	[Header("Walk")]
	[SerializeField] private float maxWalkSpeed = 10;
	// time to get from 0 to maxWalkSpeed (and max to 0)
	[SerializeField] private float accelerateTime = 0.2f;
	
	[Header("Jump")]
	// units the player can jump high
	[SerializeField] private float jumpHeight = 3.25f;
	// time buffer in which player still jumps after not touching ground anymore or jump before even touching the ground
	[SerializeField] private float jumpRememberDuration = 0.2f;
	
	[Header("Crouch")]
	[SerializeField] private float maxCrouchSpeed = 4f;
	[SerializeField] private float crouchHeight = .9f;
	[SerializeField] private Transform ceilingCheck;
	
	private float _jumpPressedRemember;
	private float _groundedRemember;
	private float _startRunTime;
	private float _stopRunTime;
	private float _stopRunVelocity;
	private bool _wasRunning;
	private bool _isFacingRight;

	private Rigidbody2D _rigid;
	private PlayerControls _controls;
	private DistanceJoint2D _tongueConnection;
	
	private float _lastMovementInput;
	private bool _jumpInputPerformed;
	private bool _isCrouching;
	private bool _wantsCrouch;
	private float _defaultHeight;
	private float _defaultHitboxOffY;

	private bool _isPlayerFacingRight = true;

	private AudioSource[] _walkingAudios;
	
	private void OnEnable() {
		_controls = new PlayerControls();
		_controls.Player.Crouch.performed += _ => _wantsCrouch = true;
		_controls.Player.Crouch.canceled += _ => _wantsCrouch = false;
		_controls.Enable();
		
		_walkingAudios = GetComponents<AudioSource>();
		_walkingAudios[0].enabled = false;
		_walkingAudios[1].enabled = false;
		_walkingAudios[2].enabled = false;
	}

	private void OnDisable() {
		_controls.Disable();
	}
	
	private void Start() {
		_rigid = GetComponent<Rigidbody2D>();
		_defaultHeight = box.size.y + 2 * box.edgeRadius;
		_defaultHitboxOffY = box.offset.y;
	}
	
	private void Update() {
		_ReadInputs();
		bool hasTurnedAround = Mathf.Sign(_rigid.velocity.x) != (_isPlayerFacingRight ? 1 : -1);

		if (!MathUtil.IsZero(_rigid.velocity.x) && hasTurnedAround) {
			_FlipBody();
		}
		_PlayMovementSounds();
	}

	private void _ReadInputs() {
		_lastMovementInput = _controls.Player.Move.ReadValue<float>();
		
		if (_controls.Player.Jump.WasPerformedThisFrame()) {
			_jumpInputPerformed = true;
		}
	}
	
	private void FixedUpdate() {
		bool isGrounded = CheckGrounding();
		
		CheckCrouching(isGrounded);
		CheckJumping(isGrounded);
		CheckHorizontalMovement();
		
		_lastMovementInput = 0;
		_jumpInputPerformed = false;
		
		bodyAnimator.SetFloat("VelY", _rigid.velocity.y);
		bodyAnimator.SetBool("Crouching", _isCrouching);
		bodyAnimator.SetFloat("Speed", isGrounded ? Mathf.Abs(_rigid.velocity.x) : 0f);
	}

	private void _PlayMovementSounds() {
		// play walking sounds
		_walkingAudios[0].enabled = _lastMovementInput != 0 && !_isCrouching;
		// play crouching sounds
		_walkingAudios[1].enabled = _lastMovementInput != 0 && _isCrouching;
	}
	public bool CheckGrounding() {
		_groundedRemember -= Time.fixedDeltaTime;
		_jumpPressedRemember -= Time.fixedDeltaTime;
		bool isGrounded = IsGrounded();
		
		if (isGrounded) {
			_groundedRemember = jumpRememberDuration;
			// disable jumping sound
			_walkingAudios[2].enabled = false;
		}
		return isGrounded;
	}

	private bool IsGrounded() {
		float edge = box.edgeRadius;
		float groundCheckHeight = transform.parent == null ? .1f : .3f;

		Vector2 boxOrigin = (Vector2) box.transform.position + box.offset - new Vector2(0, groundCheckHeight);
		Vector2 boxSize = box.size + new Vector2(2*edge, 2*edge);
		//shrinks hitbox width to avoid wall jumps
		boxSize.x -= 0.1f;

		Physics2D.queriesHitTriggers = false;
		bool isGrounded = Physics2D.OverlapBox(boxOrigin, boxSize, 0, solidsLayerMask);
		Physics2D.queriesHitTriggers = true;
		return isGrounded;
	}
	
	private void CheckJumping(bool isGrounded) {
		if (_jumpInputPerformed) {
			_jumpPressedRemember = jumpRememberDuration;
		}
		//performes jump (with small threshold before landing and after starting to fall)
		if(_jumpPressedRemember > 0 && _groundedRemember > 0) {
			ApplyJumpVelocity();
			_wantsCrouch = false;
			CheckCrouching(isGrounded);
			// enable jumping sound
			_walkingAudios[2].enabled = true;
		}		
	}
	
	private void CheckHorizontalMovement() {
		float horizontalInput = _lastMovementInput;
		_rigid.velocity = CalcWalkVelocity(_rigid.velocity, horizontalInput);
	}
	/// <summary>
	/// Accelerates and decelerates player based on if horizontal movement input was performed
	/// </summary>
	/// <param name="velocity">current velocity of player</param>
	/// <param name="horizontalInput">-1 or 1 if A or D pressed</param>
	/// <returns></returns>
	private Vector2 CalcWalkVelocity(Vector2 velocity, float horizontalInput) {
		Vector2 newVelocity = new Vector2(0, velocity.y);
		
		//slows player down if no movement input
		if (MathUtil.IsZero(horizontalInput)) {
			newVelocity.x = GetDecelerated(velocity.x);
		} else {
			bool isTurningAround = Mathf.Sign(horizontalInput) != Mathf.Sign(velocity.x);
			
			//slows down player if not standing still already
			if (!MathUtil.IsZero(velocity.x) && isTurningAround) {
				newVelocity.x = 0;
			}else {
				newVelocity.x = GetAccelerated(velocity.x, Mathf.Sign(horizontalInput));
			}
		}
		return newVelocity;
	}
	
	//approximates velocity needed to reach given jump height
	/*
	 * Velocity is calculated every update, so perfectly calculating it probably won't work.
	 * Assuming that the velocity is calculated each update with: vNew = (v - g) / (1 + drag)
	 * and the formula for the start velocity needed in perpendicular throw to reach a certain height is: v = sqrt(yMax * 2 * g)
	 * perhaps they can be combined like vJump = sqrt(yMax * 2 * g * (1 + 0.5 * drag))
	 */
	private void ApplyJumpVelocity() {
		_jumpPressedRemember = 0;
		_groundedRemember = 0;
		float gravity = _rigid.gravityScale * -Physics2D.gravity.y;
		float dragFactor = (1 + 0.5f * _rigid.drag);
		float velocity = Mathf.Sqrt(jumpHeight * 2 * gravity * dragFactor);
		_rigid.velocity = new Vector2(_rigid.velocity.x, velocity);
	}
	
	/// <summary>
	/// Accelerates the current speed linearly
	/// </summary>
	/// <param name="currentSpeed"></param>
	/// <param name="direction">+1 or -1 for left or right</param>
	/// <returns></returns>
	private float GetAccelerated(float currentSpeed, float direction) {
		float maxMovementSpeed = _isCrouching ? maxCrouchSpeed : maxWalkSpeed;
		float acceleration = Time.fixedDeltaTime / accelerateTime * maxMovementSpeed;
		float newSpeed = currentSpeed + direction * acceleration;
		return Math.Clamp(newSpeed, -maxMovementSpeed, maxMovementSpeed);
	}

	private float GetDecelerated(float currentSpeed) {
		float maxMovementSpeed = _isCrouching ? maxCrouchSpeed : maxWalkSpeed;
		float deceleration = Time.fixedDeltaTime / accelerateTime * maxMovementSpeed;

		if (Mathf.Abs(currentSpeed) < deceleration) {
			return 0;
		}
		float newSpeed = currentSpeed - Mathf.Sign(currentSpeed) * deceleration;
		return Math.Clamp(newSpeed, -maxMovementSpeed, maxMovementSpeed);
	}

	/// <summary>
	/// Makes player crouch on key press. Makes player stand up on key release if not trapped below something
	/// </summary>
	private void CheckCrouching(bool isGrounded) {
		bool canStandUp = CanStandUp();
		
		if (!_isCrouching && isGrounded && (!canStandUp || _wantsCrouch)) {
			Crouch();
		}
		if (!_wantsCrouch && _isCrouching && canStandUp) {
			_StandUp();
		}
	}
	
	/// <summary>
	/// Resizes hitbox to fit crouching height and slows player down
	/// </summary>
	private void Crouch() {
		_isCrouching = true;
		box.size = new Vector2(box.size.x, crouchHeight - 2 * box.edgeRadius);
		box.offset = new Vector2(box.offset.x, _defaultHitboxOffY - (_defaultHeight - crouchHeight) / 2);
		
		_rigid.velocity = new Vector2(
			Mathf.Clamp(_rigid.velocity.x, -maxCrouchSpeed, maxCrouchSpeed),
			_rigid.velocity.y);
	}

	/// <summary>
	/// Checks intersection ceiling check with scene
	/// </summary>
	/// <returns>true if nothing is blocking the player from standing up, otherwise false</returns>
	private bool CanStandUp() {
		Physics2D.queriesHitTriggers = false;
		bool canStandUp = !Physics2D.OverlapPoint(ceilingCheck.position, solidsLayerMask);
		Physics2D.queriesHitTriggers = true;
		return canStandUp;
	}

	/// <summary>
	/// Resized hitbox back to default size.
	/// </summary>
	private void _StandUp() {
		_isCrouching = false;
		box.size = new Vector2(box.size.x, _defaultHeight - 2 * box.edgeRadius);
		box.offset = new Vector2(box.offset.x, _defaultHitboxOffY);
	}
	
	private void _FlipBody() {
		bodyRenderer.transform.localScale = _InvertX(bodyRenderer.transform.localScale);
		_isPlayerFacingRight = !_isPlayerFacingRight;
	}
	
	private Vector3 _InvertX(Vector3 v) {
		return new Vector3(-v.x, v.y, v.z);
	}
}